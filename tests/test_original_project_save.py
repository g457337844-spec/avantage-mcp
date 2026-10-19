from contextlib import nullcontext
from pathlib import Path
import shutil
import pytest
from avantage_mcp.bridge_client import BridgeError
from avantage_mcp import project_writeback as module

@pytest.fixture(autouse=True)
def isolated_recovery(tmp_path, monkeypatch):
    monkeypatch.setenv('AVANTAGE_RECOVERY_ROOT', str(tmp_path / 'recovery'))

def bundle(path):
    path.write_text('original')
    path.with_suffix('.DATA').mkdir()
    (path.with_suffix('.DATA')/'raw.vgd').write_bytes(b'raw')
    return path

class Client:
    def __init__(self,path,verified=True):self.path=path;self.verified=verified;self.calls=[]
    def exclusive(self):return nullcontext()
    def call(self,op,args=None):
        self.calls.append((op,args))
        if op=='status':return {'native_project_save_verified':True}
        if op=='get_spectrum_info':return {'source_path':str(self.path)}
        if op=='save_project_model':
            target=Path(args['path']);shutil.copy2(self.path,target)
            shutil.copytree(self.path.with_suffix('.DATA'),target.with_suffix('.DATA'))
            target.write_text('new model')
            return {key:self.verified for key in ('roundtrip_verified','raw_data_verified','unrelated_spectra_verified','grid_references_verified')}
        raise AssertionError(op)

def test_original_save_backup_and_no_live_window_calls(tmp_path,monkeypatch):
    path=bundle(tmp_path/'2.vgp');client=Client(path)
    monkeypatch.setattr(module,'require_unopened_original',lambda _:None)
    before=module.manifest(path);result=module.save_project_model_in_place(client,'s1')
    assert path.read_text()=='new model' and result['application_windows_opened']==0
    assert module.manifest(Path(result['backup_path']))==before
    assert [op for op,_ in client.calls]==['status','get_spectrum_info','save_project_model']

def test_open_original_rejected_before_native_save(tmp_path,monkeypatch):
    path=bundle(tmp_path/'2.vgp');client=Client(path);before=module.manifest(path)
    def busy(_):raise BridgeError('ORIGINAL_IN_USE','Close the original')
    monkeypatch.setattr(module,'require_unopened_original',busy)
    with pytest.raises(BridgeError,match='ORIGINAL_IN_USE'):module.save_project_model_in_place(client,'s1')
    assert not any(op=='save_project_model' for op,_ in client.calls)
    assert module.manifest(path)==before

def test_incomplete_native_verification_never_publishes(tmp_path,monkeypatch):
    path=bundle(tmp_path/'2.vgp');client=Client(path,False);before=module.manifest(path)
    monkeypatch.setattr(module,'require_unopened_original',lambda _:None)
    with pytest.raises(BridgeError,match='PROJECT_SAVE_UNVERIFIED'):module.save_project_model_in_place(client,'s1')
    assert module.manifest(path)==before

def test_windows_open_file_guard(tmp_path):
    if module.os.name!='nt':pytest.skip('Windows file sharing check')
    path=bundle(tmp_path/'2.vgp')
    module.require_unopened_original(path)
    with path.open('rb'):
        with pytest.raises(BridgeError,match='ORIGINAL_IN_USE'):module.require_unopened_original(path)
