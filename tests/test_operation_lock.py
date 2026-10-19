import os
import subprocess
import sys

import pytest

from avantage_mcp.operation_lock import live_transaction


@pytest.mark.skipif(os.name!="nt",reason="Windows cross-process application transaction")
def test_transaction_is_recursive_but_excludes_other_processes():
    probe="""
from avantage_mcp.operation_lock import live_transaction
from avantage_mcp.bridge_client import BridgeError
try:
    with live_transaction():print('acquired')
except BridgeError as error:print(error.code)
"""
    def other():
        return subprocess.run([sys.executable,"-c",probe],capture_output=True,text=True,check=True,timeout=10).stdout.strip()
    with live_transaction():
        with live_transaction():assert other()=="LIVE_OPERATION_BUSY"
        assert other()=="LIVE_OPERATION_BUSY"
    assert other()=="acquired"
