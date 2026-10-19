from contextlib import nullcontext
from pathlib import Path
import pytest
from avantage_mcp.bridge_client import BridgeError
import avantage_mcp.project_writeback as module

def bundle(parent, content='original'):
    parent.mkdir(parents=True)
    path=parent/'2.vgp';path.write_text(content)
    data=path.with_suffix('.DATA');data.mkdir()
    (data/'raw.vgd').write_text(content+'-raw')
    return path

class Client:
    def __init__(self,path,lifecycle=True,fail_save=False,change_on_apply=False):
        self.path=path;self.lifecycle=lifecycle;self.fail_save=fail_save
        self.calls=[];self.change_on_apply=change_on_apply
    def exclusive(self):return nullcontext()
    def call(self,op,args=None):
        args=args or {};self.calls.append((op,args))
        if op=='status':return {'native_live_document_bridge_verified':True,'native_document_lifecycle_verified':self.lifecycle}
        if op=='list_processing_documents':return {'documents':[{'document_id':'source','path':str(self.path)}]}
        if op=='get_spectrum_info':return {'source_path':str(self.path)}
        if op=='apply_fit_to_document':
            if self.change_on_apply:self.path.write_text('concurrent user save')
            return {'document':{'document_id':'source'},'live_readback_verified':True,'saved_project_verified':False}
        if op=='save_processing_document_existing':
            self.path.write_text('new model')
            if self.fail_save:raise RuntimeError('save verification failed')
            return {'document':{'document_id':'source','path':str(self.path),'modified':False},**{key:True for key in ('roundtrip_verified','live_readback_verified','raw_data_verified','all_spectrum_models_verified','grid_bindings_verified','same_window_retained','no_extra_windows_verified','saved_state_verified')}}
        raise AssertionError(op)

def test_original_save_retains_window_and_full_backup(tmp_path):
    path=bundle(tmp_path/'original');before=module.manifest(path);client=Client(path)
    result=module.save_document_in_place(client,'source',str(path))
    assert result['same_window_retained'] and result['saved_state_verified']
    assert result['document']['modified'] is False
    assert module.manifest(Path(result['backup_path']))==before
    assert path.read_text()=='new model'
    assert [op for op,_ in client.calls]==['status','list_processing_documents','save_processing_document_existing']

def test_old_build_rejected_before_any_save(tmp_path):
    path=bundle(tmp_path/'original');before=module.manifest(path);client=Client(path,lifecycle=False)
    with pytest.raises(BridgeError,match='DOCUMENT_LIFECYCLE_NOT_VERIFIED'):
        module.save_document_in_place(client,'source',str(path))
    assert client.calls==[('status',{})] and module.manifest(path)==before

def test_failed_save_retains_backup_and_never_retries(tmp_path):
    path=bundle(tmp_path/'original');client=Client(path,fail_save=True)
    with pytest.raises(BridgeError,match='ORIGINAL_SAVE_UNVERIFIED') as error:
        module.save_document_in_place(client,'source',str(path))
    assert Path(error.value.details['backup_path']).read_text()=='original'
    assert sum(op=='save_processing_document_existing' for op,_ in client.calls)==1

def test_wrong_original_path_rejected(tmp_path):
    path=bundle(tmp_path/'original');other=bundle(tmp_path/'other');client=Client(path)
    with pytest.raises(ValueError,match='own original path'):
        module.save_document_in_place(client,'source',str(other))
    assert not any(op=='save_processing_document_existing' for op,_ in client.calls)

def test_default_apply_completes_original_save(tmp_path):
    path=bundle(tmp_path/'original');client=Client(path)
    result=module.apply_document_model(client,'s1')
    assert result['in_place'] and result['no_extra_windows_verified'] and result['saved_project_verified']
    assert result['document']['document_id']=='source' and Path(result['backup_path']).read_text()=='original'

def test_explicit_unsaved_live_apply_does_not_save_source(tmp_path):
    path=bundle(tmp_path/'original');client=Client(path)
    result=module.apply_document_model(client,'s1',False)
    assert result['document']['document_id']=='source' and not result['saved_project_verified']
    assert [op for op,_ in client.calls]==['status','apply_fit_to_document']

def test_private_intermediate_rejected(tmp_path,monkeypatch):
    path=bundle(tmp_path/'private');client=Client(path);monkeypatch.setenv('AVANTAGE_WORK_DIR',str(path.parent))
    with pytest.raises(BridgeError,match='ORIGINAL_PATH_REQUIRED'):
        module.apply_document_model(client,'s1')
    assert not any(op=='apply_fit_to_document' for op,_ in client.calls)

def test_concurrent_user_save_never_overwritten(tmp_path):
    path=bundle(tmp_path/'original');client=Client(path,change_on_apply=True)
    with pytest.raises(BridgeError,match='ORIGINAL_CHANGED'):
        module.apply_document_model(client,'s1')
    assert path.read_text()=='concurrent user save'
    assert not any(op=='save_processing_document_existing' for op,_ in client.calls)
