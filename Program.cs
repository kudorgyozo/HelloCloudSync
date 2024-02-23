// See https://aka.ms/new-console-template for more information
using System.Security.Principal;
using Windows.Storage.Provider;
using WinRT;
using Vanara.PInvoke;
using Vanara;
using Vanara.InteropServices;
using System.DirectoryServices.ActiveDirectory;
using System.Diagnostics;
using static Vanara.PInvoke.CldApi;
using Vanara.Extensions;
using static Vanara.PInvoke.SearchApi;
using System.Runtime.InteropServices;
using System.Text;

const string storageId = "GyozoId";
const string clientPath = @"F:\GyozoDrive";

const string contents = "Lorem Ipsum is simply dummy text of the printing and typesetting industry. Lorem Ipsum has been the industry's standard dummy text";




CF_CONNECTION_KEY connectionKey = new CF_CONNECTION_KEY();

var res = StorageProviderSyncRootManager.GetCurrentSyncRoots();
if (res.Any()) {
    Console.WriteLine("===========unregister previous sync root");
    StorageProviderSyncRootManager.Unregister(storageId);
    Thread.Sleep(1000);
    return 0;
}

void AddFolderToSearch() {
    //Uri path = new Uri(location);

    //string indexingPath = path.AbsoluteUri;

    //CSearchManager csm = new CSearchManager();
    //CSearchCrawlScopeManager manager = csm.GetCatalog("SystemIndex").GetCrawlScopeManager();
    //manager.AddUserScopeRule(indexingPath, 1, 1, 0);
    //manager.SaveAll();
}

await RegisterWithShell();

ConnectSyncRootCallbacks();

CreatePlaceholders();

ListenForKeyPress();

DisconnectSyncRootCallbacks();
StorageProviderSyncRootManager.Unregister(storageId);

Console.Write("=======exit");



void ListenForKeyPress() {
    Console.WriteLine("ListenForKeyPress");

    //TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>();
    bool cancel = false;
    Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) => {
        //tcs.TrySetResult(false);
        cancel = true;
        e.Cancel = true;
    };
    //tcs.Task.Wait();

    while (!cancel) {
        Console.Write('.');
        Thread.Sleep(1000);
    }
    Console.WriteLine();
    Console.WriteLine("ListenForKeyPress END");
    //var res = tcs.Task.Result;
}

static async Task RegisterWithShell() {
    Console.WriteLine("register with shell {0}", clientPath);
    var info = new StorageProviderSyncRootInfo {
        Id = storageId,
        RecycleBinUri = new Uri("http://cloudmirror.example.com/recyclebin"),
        IconResource = "%SystemRoot%\\system32\\charmap.exe,0",
        Path = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(clientPath),
        DisplayNameResource = "GyozoDrive",
        HydrationPolicy = StorageProviderHydrationPolicy.Full, 
        HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
        PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull, //=======*************
        InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
        Version = "1.0.0",
        ShowSiblingsAsGroup = false,
        HardlinkPolicy = StorageProviderHardlinkPolicy.None,
    };

    var buf = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(System.Text.Encoding.UTF8.GetBytes("GyozoDrive"));
    info.Context = buf;

    StorageProviderSyncRootManager.Register(info);
    Thread.Sleep(1000);
}

void ConnectSyncRootCallbacks() {
    Console.WriteLine("Connect sync root callbacks {0}", clientPath);

    var callbacks = new CF_CALLBACK_REGISTRATION[] {
        new CF_CALLBACK_REGISTRATION() { Callback = OnFetchData, Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA },
        new CF_CALLBACK_REGISTRATION() { Callback = OnCancelFetch, Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA },
        new CF_CALLBACK_REGISTRATION() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
    };

    var res = CfConnectSyncRoot(clientPath, callbacks, IntPtr.Zero,
        CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO,
    out connectionKey);
    Thread.Sleep(1000);
    Console.WriteLine(res);
}

void DisconnectSyncRootCallbacks() {
    Console.WriteLine($"Disconnect sync root callbacks");
    var res = CfDisconnectSyncRoot(connectionKey);
    Thread.Sleep(1000);
    Console.WriteLine(res);

}

void OnCancelFetch(in CF_CALLBACK_INFO CallbackInfo, in CF_CALLBACK_PARAMETERS CallbackParameters) {
    Console.WriteLine("cancel fetch");
}


void OnFetchData(in CF_CALLBACK_INFO CallbackInfo, in CF_CALLBACK_PARAMETERS CallbackParameters) {
    Console.WriteLine($"fetch lo: {CallbackParameters.FetchData.OptionalLength} lr: {CallbackParameters.FetchData.RequiredLength}");
    var bytes = Encoding.UTF8.GetBytes(contents);

    CfReportProviderProgress(connectionKey, CallbackInfo.TransferKey, bytes.Length, bytes.Length);

    CF_OPERATION_INFO info = new CF_OPERATION_INFO() {
        Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
        StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
        ConnectionKey = connectionKey,
        TransferKey = CallbackInfo.TransferKey,
    };

    var ms = new NativeMemoryStream();
    ms.Seek(0, SeekOrigin.Begin);
    ms.Position = 0;
    ms.SetLength(0);
    ms.Write(bytes);
    ms.Flush();

    CF_OPERATION_PARAMETERS pars = new CF_OPERATION_PARAMETERS() {
        ParamSize = CF_OPERATION_PARAMETERS.CF_SIZE_OF_OP_PARAM<CF_OPERATION_PARAMETERS.TRANSFERDATA>(),
        TransferData = new CF_OPERATION_PARAMETERS.TRANSFERDATA {
            CompletionStatus = NTStatus.STATUS_SUCCESS,
            Buffer = ms.Pointer,
            Length = bytes.Length,
            Offset = 0,
        }
    };

    var result = CfExecute(info, ref pars);
    Console.WriteLine(result);

}

static void CreatePlaceholders() {
    Console.WriteLine("create placeholders");
    uint entries = 0;
    var date = new DateTime(2024, 01, 01);

    SafeLPWSTR fileNameStr = new SafeLPWSTR("ez.txt");

    CF_PLACEHOLDER_CREATE_INFO cldInfo = new CF_PLACEHOLDER_CREATE_INFO() {
        FileIdentity = fileNameStr,
        FileIdentityLength = fileNameStr.Size,
        Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
        FsMetadata = new CF_FS_METADATA {
            BasicInfo = new Kernel32.FILE_BASIC_INFO {
                ChangeTime = FileTimeExtensions.ToFileTimeStruct(date),
                CreationTime = FileTimeExtensions.ToFileTimeStruct(date),
                LastAccessTime = FileTimeExtensions.ToFileTimeStruct(date),
                LastWriteTime = FileTimeExtensions.ToFileTimeStruct(date),
                FileAttributes = FileFlagsAndAttributes.FILE_ATTRIBUTE_NORMAL
            },
            FileSize = Encoding.UTF8.GetBytes(contents).Length,
        },
        RelativeFileName = "ez.txt",
    };

    var result = CfCreatePlaceholders($"{clientPath}", [cldInfo], 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out entries);
    Thread.Sleep(1000);
    Console.WriteLine(result);
    fileNameStr.Dispose();
}


return 0;