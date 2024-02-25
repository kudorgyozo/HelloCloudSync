// See https://aka.ms/new-console-template for more information
using Windows.Storage.Provider;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;


const string storageId = "GyozoId";
const string driveDisplayName = "GyozoDrive";

const string clientRootPath = @$"F:\GyozoDrive";
const string serverRootPath = @$"F:\GyozoCloud";
const int bufferSize = 1024 * 4;
var buffer = new byte[bufferSize];

CF_CONNECTION_KEY connectionKey = new CF_CONNECTION_KEY();

var res = StorageProviderSyncRootManager.GetCurrentSyncRoots();

if (res.Any()) {
    Console.WriteLine("===========unregister previous sync root");
    StorageProviderSyncRootManager.Unregister(storageId);
    Thread.Sleep(1000);
}

await RegisterWithShell();
ConnectSyncRootCallbacks();
await ListenForKeyPress();
DisconnectSyncRootCallbacks();
StorageProviderSyncRootManager.Unregister(storageId);
Console.WriteLine("=======exit");

async Task ListenForKeyPress() {
    Console.WriteLine("ListenForKeyPress");

    TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(false);
    //bool cancel = false;
    Console.CancelKeyPress += (object sender, ConsoleCancelEventArgs e) => {
        tcs.TrySetResult(true);
        //cancel = true;
        e.Cancel = true;
    };
    await tcs.Task;

    //while (!cancel) {
    //Console.Write('.');
    //Thread.Sleep(1000);
    //}
    Console.WriteLine("ListenForKeyPress END");
    //var res = tcs.Task.Result;
}

static async Task RegisterWithShell() {
    Console.WriteLine("register with shell {0}", clientRootPath);
    var info = new StorageProviderSyncRootInfo {
        Id = storageId,
        //RecycleBinUri = new Uri("http://cloudmirror.example.com/recyclebin"),
        IconResource = "%SystemRoot%\\system32\\charmap.exe,0", //a valid icon is absolutely crucial apparently, otherwise it crashes
        Path = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(clientRootPath),
        DisplayNameResource = "GyozoDrive",
        HydrationPolicy = StorageProviderHydrationPolicy.Full,
        HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
        PopulationPolicy = StorageProviderPopulationPolicy.Full,
        InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
        Version = "1.0.0",
        ShowSiblingsAsGroup = false,
        HardlinkPolicy = StorageProviderHardlinkPolicy.None,
    };

    var buf = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(Encoding.UTF8.GetBytes("GyozoDrive"));
    info.Context = buf;

    StorageProviderSyncRootManager.Register(info);
    Thread.Sleep(1000);
}

void ConnectSyncRootCallbacks() {
    Console.WriteLine("Connect sync root callbacks {0}", clientRootPath);

    unsafe {
        var callbacks = new CF_CALLBACK_REGISTRATION[] {
            new CF_CALLBACK_REGISTRATION() { Callback = OnFetchData, Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA },
            new CF_CALLBACK_REGISTRATION() { Callback = OnCancelFetch, Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA },
            new CF_CALLBACK_REGISTRATION() { Callback = OnFetchPlaceholders, Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS },
            new CF_CALLBACK_REGISTRATION() { Callback = OnCancelFetchPlaceholders, Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS },
            new CF_CALLBACK_REGISTRATION() { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
        };

        var result = PInvoke.CfConnectSyncRoot(clientRootPath, callbacks, (void*)null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO,
            out connectionKey);
        Console.WriteLine(result);
    }
    Thread.Sleep(500);
}

unsafe void OnCancelFetchPlaceholders(CF_CALLBACK_INFO* CallbackInfo, CF_CALLBACK_PARAMETERS* CallbackParameters) {
    Console.WriteLine($"OnCancelFetchPlaceholders {CallbackInfo->NormalizedPath}");
}

unsafe void OnFetchPlaceholders(CF_CALLBACK_INFO* CallbackInfo, CF_CALLBACK_PARAMETERS* CallbackParameters) {
    Console.WriteLine($"OnFetchPlaceholders {CallbackInfo->NormalizedPath}");

    var clientPath = CallbackInfo->VolumeDosName.ToString() + CallbackInfo->NormalizedPath.ToString();
    var serverPath = clientPath.Replace(clientRootPath, serverRootPath);

    CF_OPERATION_INFO info = new CF_OPERATION_INFO() {
        Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
        StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
        ConnectionKey = connectionKey,
        TransferKey = CallbackInfo->TransferKey,
    };
    var dirInfo = new DirectoryInfo(serverPath);
    var files = dirInfo.EnumerateFileSystemInfos().ToArray();

    var names = files.Select(f => f.Name).ToArray();
    GCHandle[] handles = new GCHandle[names.Length];
    try {
        CF_PLACEHOLDER_CREATE_INFO[] cldInfo = new CF_PLACEHOLDER_CREATE_INFO[files.Length];

        for (int i = 0; i < cldInfo.Length; i++) {
            var res = new CF_PLACEHOLDER_CREATE_INFO();
            var entry = files[i];
            var fileNameBytes = Encoding.UTF8.GetBytes(entry.Name);

            //https://www.phind.com/search?cache=g4jfmiyaojbodt2n9lxhcciy
            //I want an entire array of things pinned into memory
            //chatGPT says another option is stackalloc, to COPY all the names onto the stack and then they are fixed by nature
            char[] charArray = names[i].ToCharArray();
            handles[i] = GCHandle.Alloc(charArray, GCHandleType.Pinned);
            res.RelativeFileName = new PCWSTR((char*)handles[i].AddrOfPinnedObject());
            res.FsMetadata = new CF_FS_METADATA {
                BasicInfo = new FILE_BASIC_INFO {
                    ChangeTime = entry.LastWriteTime.ToFileTime(),
                    CreationTime = entry.CreationTime.ToFileTime(),
                    LastAccessTime = entry.LastAccessTime.ToFileTime(),
                    LastWriteTime = entry.LastWriteTime.ToFileTime(),
                },
            };
            res.Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;

            var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
            //I need to put something here but I dont need it

            res.FileIdentity = (void*)handles[i].AddrOfPinnedObject();
            res.FileIdentityLength = (uint)(charArray.Length * sizeof(char));
            if (entry is FileInfo file) {
                res.FsMetadata.FileSize = file.Length;
                res.FsMetadata.BasicInfo.FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;
            } else {
                res.FsMetadata.FileSize = 0;
                res.FsMetadata.BasicInfo.FileAttributes = (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY;
            }
            cldInfo[i] = res;
        }

        CF_OPERATION_PARAMETERS pars = new CF_OPERATION_PARAMETERS();

        // Paramsize = offset + size into a struct. The struct contains only paramsize field and a union of structs of different sizes one for each type of operation you can do with cf execute.
        // (This looks like a pretty dumb way to design an OS API.
        // 1. Can't you just add a single byte after paramSize to identify the type of struct?
        // 2. WHY do I need to provide a paramSize in the first place. Everything inside the struct has a constant length in memory. ParamSize is known at compile time.
        // 3. why have only one method, cfExecute, with multiple parameter types that do totally different things? Why not create separate methods for each action?)

        pars.ParamSize = (uint)(Marshal.OffsetOf<CF_OPERATION_PARAMETERS>("Anonymous") + Marshal.SizeOf<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferPlaceholders_e__Struct>());
        pars.Anonymous.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;
        //pars.Anonymous.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE;
        pars.Anonymous.TransferPlaceholders.PlaceholderTotalCount = cldInfo.Length;
        pars.Anonymous.TransferPlaceholders.PlaceholderCount = (uint)cldInfo.Length;
        fixed (CF_PLACEHOLDER_CREATE_INFO* pCldInfo = cldInfo) {
            pars.Anonymous.TransferPlaceholders.PlaceholderArray = pCldInfo;
            pars.Anonymous.TransferPlaceholders.CompletionStatus = new NTSTATUS((int)NTSTATUS.Severity.Success);
            var result = PInvoke.CfExecute(info, ref pars);
            Console.WriteLine(result);
        }
    } finally {
        // Free the GCHandles to allow the garbage collector to move the objects
        for (int i = 0; i < handles.Length; i++) {
            if (handles[i].IsAllocated)
                handles[i].Free();
        }
    }




}

unsafe void OnCancelFetch(CF_CALLBACK_INFO* CallbackInfo, CF_CALLBACK_PARAMETERS* CallbackParameters) {
    Console.WriteLine("cancel fetch");
}



unsafe void OnFetchData(CF_CALLBACK_INFO* CallbackInfo, CF_CALLBACK_PARAMETERS* CallbackParameters) {
    Console.WriteLine($"fetch {CallbackInfo->NormalizedPath}");

    var clientPath = CallbackInfo->VolumeDosName.ToString() + CallbackInfo->NormalizedPath.ToString();
    var serverPath = clientPath.Replace(clientRootPath, serverRootPath);

    using (FileStream fs = new FileStream(serverPath, FileMode.Open)) {

        int destOffset = 0;
        int bytesRead = 0;

        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0) {

            // Process the chunk of data read into the buffer
            Console.WriteLine($"Read {bytesRead} bytes.");

            PInvoke.CfReportProviderProgress(connectionKey, CallbackInfo->TransferKey, fs.Length, destOffset + bytesRead);

            CF_OPERATION_INFO info = new CF_OPERATION_INFO() {
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                ConnectionKey = connectionKey,
                TransferKey = CallbackInfo->TransferKey,
            };

            fixed (byte* pBuffer = buffer) {
                CF_OPERATION_PARAMETERS pars = new CF_OPERATION_PARAMETERS();
                pars.ParamSize = (uint)(Marshal.OffsetOf<CF_OPERATION_PARAMETERS>("Anonymous") + Marshal.SizeOf<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct>());

                pars.Anonymous.TransferData.Buffer = pBuffer;
                pars.Anonymous.TransferData.Length = bytesRead;
                pars.Anonymous.TransferData.Offset = destOffset;
                pars.Anonymous.TransferData.CompletionStatus = new NTSTATUS((int)NTSTATUS.Severity.Success);
                var result = PInvoke.CfExecute(info, ref pars);
                Console.WriteLine(result);
            }
            destOffset += bytesRead;
            Thread.Sleep(250);
        }

    }

}

void DisconnectSyncRootCallbacks() {
    Console.WriteLine($"Disconnect sync root callbacks");
    var res = PInvoke.CfDisconnectSyncRoot(connectionKey);
    Thread.Sleep(1000);
    Console.WriteLine(res);

}

return 0;