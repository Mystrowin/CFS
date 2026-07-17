#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <projectedfslib.h>
#using <System.dll>
#using "Cfs.Core.dll"
#include <vcclr.h>
using namespace System;
using namespace Cfs::Core;
public ref class Bridge sealed {
public: static String^ Archive = nullptr; static String^ Entry = nullptr; static array<Byte>^ Data = nullptr;
};
static HRESULT CALLBACK Placeholder(const PRJ_CALLBACK_DATA* d) { if (gcnew String(d->FilePathName) != Bridge::Entry) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND); PRJ_PLACEHOLDER_INFO i={}; i.FileBasicInfo.FileSize=Bridge::Data->LongLength; i.FileBasicInfo.FileAttributes=FILE_ATTRIBUTE_NORMAL; return PrjWritePlaceholderInfo(d->NamespaceVirtualizationContext,d->FilePathName,&i,sizeof(i)); }
static HRESULT CALLBACK Data(const PRJ_CALLBACK_DATA* d,UINT64 o,UINT32 n) { if (gcnew String(d->FilePathName)!=Bridge::Entry || o>=Bridge::Data->LongLength) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND); n=(UINT32)Math::Min((Int64)n,Bridge::Data->LongLength-(Int64)o); void* b=PrjAllocateAlignedBuffer(d->NamespaceVirtualizationContext,n); if(!b)return E_OUTOFMEMORY; pin_ptr<Byte> p=&Bridge::Data[(int)o]; memcpy(b,p,n); HRESULT h=PrjWriteFileData(d->NamespaceVirtualizationContext,&d->DataStreamId,b,o,n); PrjFreeAlignedBuffer(b); return h; }
static HRESULT CALLBACK Start(const PRJ_CALLBACK_DATA*,const GUID*){return S_OK;} static HRESULT CALLBACK End(const PRJ_CALLBACK_DATA*,const GUID*){return S_OK;}
static HRESULT CALLBACK Enumerate(const PRJ_CALLBACK_DATA*,const GUID*,PCWSTR,PRJ_DIR_ENTRY_BUFFER_HANDLE h){PRJ_FILE_BASIC_INFO i={};i.FileSize=Bridge::Data->LongLength;i.FileAttributes=FILE_ATTRIBUTE_NORMAL;pin_ptr<const wchar_t> p=PtrToStringChars(Bridge::Entry);return PrjFillDirEntryBuffer(p,&i,h);}
int wmain(int c,wchar_t**v){if(c!=4)return 2; Bridge::Archive=gcnew String(v[1]);Bridge::Entry=gcnew String(v[2]);auto e=CfsArchive::LoadManifestEntries(Bridge::Archive);CfsEntry^ x=nullptr;for each(CfsEntry^ z in e)if(z->Path==Bridge::Entry){x=z;break;}if(x==nullptr)return 3;Bridge::Data=CfsArchive::ReadManifestEntry(Bridge::Archive,x);GUID id;CoCreateGuid(&id);CreateDirectoryW(v[3],0);if(FAILED(PrjMarkDirectoryAsPlaceholder(v[3],0,0,&id)))return 4;PRJ_CALLBACKS cb={Start,End,Enumerate,Placeholder,Data,0,0,0};PRJ_NAMESPACE_VIRTUALIZATION_CONTEXT ctx;if(FAILED(PrjStartVirtualizing(v[3],&cb,0,0,&ctx)))return 5;wchar_t p[MAX_PATH];wsprintfW(p,L"%s\\%s",v[3],v[2]);HANDLE f=CreateFileW(p,GENERIC_READ,0,0,OPEN_EXISTING,0,0);char b[1];DWORD n=0;if(f!=INVALID_HANDLE_VALUE){ReadFile(f,b,1,&n,0);CloseHandle(f);}PrjStopVirtualizing(ctx);return n==1?0:6;}
