"""Serialize application transactions across local MCP server processes."""
from contextlib import contextmanager
import ctypes
import os


LIVE_OPERATIONS=frozenset({
    "list_processing_documents","list_document_spectra","attach_document_spectrum",
    "apply_fit_to_document","save_processing_document","save_processing_document_existing",
    "open_processing_document","open_processing_project",
})


@contextmanager
def live_transaction():
    if os.name!="nt":
        yield
        return
    kernel=ctypes.WinDLL("kernel32",use_last_error=True)
    kernel.CreateMutexW.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_wchar_p]
    kernel.CreateMutexW.restype=ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes=[ctypes.c_void_p,ctypes.c_uint32]
    kernel.WaitForSingleObject.restype=ctypes.c_uint32
    kernel.ReleaseMutex.argtypes=[ctypes.c_void_p]
    kernel.CloseHandle.argtypes=[ctypes.c_void_p]
    handle=kernel.CreateMutexW(None,False,r"Local\AvantageMCP.LiveTransaction")
    if not handle:raise ctypes.WinError(ctypes.get_last_error())
    acquired=False
    try:
        result=kernel.WaitForSingleObject(handle,0)
        if result not in (0,0x80):
            if result==0x102:
                from .bridge_client import BridgeError
                raise BridgeError("LIVE_OPERATION_BUSY","Another MCP process is using Avantage; wait for it to finish. No application call was sent.")
            raise ctypes.WinError(ctypes.get_last_error())
        acquired=True
        yield
    finally:
        if acquired:kernel.ReleaseMutex(handle)
        kernel.CloseHandle(handle)
