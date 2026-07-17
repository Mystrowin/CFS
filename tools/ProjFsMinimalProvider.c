#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <projectedfslib.h>

static PRJ_NAMESPACE_VIRTUALIZATION_CONTEXT g_ctx;
static const char g_data[] = "CFS ProjFS hydration test\r\n";

static HRESULT CALLBACK GetPlaceholder(const PRJ_CALLBACK_DATA *d) {
  if (_wcsicmp(d->FilePathName, L"test.txt")) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
  PRJ_PLACEHOLDER_INFO info = {0};
  info.FileBasicInfo.FileSize = sizeof(g_data) - 1;
  info.FileBasicInfo.FileAttributes = FILE_ATTRIBUTE_NORMAL;
  return PrjWritePlaceholderInfo(d->NamespaceVirtualizationContext, d->FilePathName, &info, sizeof(info));
}
static HRESULT CALLBACK GetData(const PRJ_CALLBACK_DATA *d, UINT64 offset, UINT32 length) {
  if (_wcsicmp(d->FilePathName, L"test.txt") || offset >= sizeof(g_data) - 1) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
  UINT32 available = (UINT32)((sizeof(g_data) - 1) - offset); if (length > available) length = available;
  void *buffer = PrjAllocateAlignedBuffer(d->NamespaceVirtualizationContext, length);
  if (!buffer) return E_OUTOFMEMORY;
  memcpy(buffer, g_data + offset, length);
  HRESULT hr = PrjWriteFileData(d->NamespaceVirtualizationContext, &d->DataStreamId, buffer, offset, length);
  PrjFreeAlignedBuffer(buffer); return hr;
}
static HRESULT CALLBACK StartEnum(const PRJ_CALLBACK_DATA *d, const GUID *id) { return S_OK; }
static HRESULT CALLBACK EndEnum(const PRJ_CALLBACK_DATA *d, const GUID *id) { return S_OK; }
static HRESULT CALLBACK GetEnum(const PRJ_CALLBACK_DATA *d, const GUID *id, PCWSTR pattern, PRJ_DIR_ENTRY_BUFFER_HANDLE h) {
  PRJ_FILE_BASIC_INFO info = {0}; info.FileSize = sizeof(g_data)-1; info.FileAttributes = FILE_ATTRIBUTE_NORMAL;
  return PrjFillDirEntryBuffer(L"test.txt", &info, h);
}
int wmain(int argc, wchar_t **argv) {
  wchar_t root[MAX_PATH]; if (argc != 2) return 2; GetFullPathNameW(argv[1], MAX_PATH, root, NULL); CreateDirectoryW(root, NULL);
  GUID id; CoCreateGuid(&id); HRESULT hr = PrjMarkDirectoryAsPlaceholder(root, NULL, NULL, &id); if (FAILED(hr)) return hr;
  PRJ_CALLBACKS cb = { StartEnum, EndEnum, GetEnum, GetPlaceholder, GetData, NULL, NULL, NULL };
  hr = PrjStartVirtualizing(root, &cb, NULL, NULL, &g_ctx); if (FAILED(hr)) return hr;
  wchar_t file[MAX_PATH]; wsprintfW(file, L"%s\\test.txt", root); HANDLE h=CreateFileW(file,GENERIC_READ,0,NULL,OPEN_EXISTING,0,NULL); char b[64]={0}; DWORD n=0; if(h!=INVALID_HANDLE_VALUE){ReadFile(h,b,sizeof(b),&n,NULL);CloseHandle(h);} PrjStopVirtualizing(g_ctx);
  return (n == sizeof(g_data)-1 && !memcmp(b,g_data,n)) ? 0 : 3;
}
