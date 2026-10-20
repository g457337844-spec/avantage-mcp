import asyncio

import pytest
from mcp.server.fastmcp.exceptions import ToolError

import avantage_mcp.server as module
from avantage_mcp.bridge_client import BridgeError
from test_tool_inventory import EXPECTED_TOOL_COUNT


@pytest.mark.parametrize('tool_name', ['avantage_save_processing_document_in_place', 'avantage_save_processing_document'])
def test_in_place_tool_schema_dispatch_and_write_annotation(monkeypatch, tool_name):
    calls=[]
    bridge=object()
    def save(client, document_id, path):
        calls.append((client,document_id,path))
        return {'in_place':True,'original_backup_verified':True}
    monkeypatch.setattr(module,'save_document_in_place',save)
    async def scenario():
        app=module.create_server(bridge)
        tools={tool.name:tool for tool in await app.list_tools()}
        assert len(tools)==EXPECTED_TOOL_COUNT
        tool=tools[tool_name]
        assert tool.annotations.destructiveHint is True
        assert tool.annotations.readOnlyHint is False
        await app.call_tool(tool.name,{'document_id':'d1','path':r'E:\数据\2.vgp'})
        with pytest.raises(ToolError):
            await app.call_tool(tool.name,{'document_id':'d1','path':'relative.vgp'})
    asyncio.run(scenario())
    assert calls==[(bridge,'d1',r'E:\数据\2.vgp')]


def test_published_but_display_unverified_error_retains_backup_details(monkeypatch):
    def fail(*args):
        raise BridgeError('SAVED_DISPLAY_UNVERIFIED','Saved; reopen manually',{'backup_path':'backup/2.vgp'})
    monkeypatch.setattr(module,'save_document_in_place',fail)
    async def scenario():
        with pytest.raises(ToolError,match='backup_path'):
            await module.create_server(object()).call_tool('avantage_save_processing_document_in_place',{'document_id':'d1','path':r'E:\数据\2.vgp'})
    asyncio.run(scenario())
