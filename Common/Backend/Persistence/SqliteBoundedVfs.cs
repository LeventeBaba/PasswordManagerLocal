using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PasswordManagerLocal.Common.Backend.Constants;
using PasswordManagerLocal.Common.Contracts.Errors;

namespace PasswordManagerLocal.Common.Backend.Persistence;

/// <summary>
/// A file-I/O shim BELOW SQLite3MC's encryption shim. Native writes, including commit,
/// spills, recovery and rekey, receive SQLITE_FULL before extending beyond a file limit.
/// Locks, reads, sync and encryption remain the platform/SQLite3MC implementations.
/// </summary>
internal static unsafe class SqliteBoundedVfs
{
    private const int Ok = 0, Full = 13, IoError = 10, NotFound = 12;
    private const int MainDatabase = 0x100, MainJournal = 0x800, Wal = 0x80000;
    private const int DeleteOnClose = 8;
    private static readonly object RegistrationGate = new();
    // Production uses one registration. Reduced test policies share registrations too.
    // SQLite owns these addresses until process exit; never unregister beneath pooled handles.
    private static readonly Dictionary<SqlitePhysicalLimits, string> Registrations = [];
    private static readonly IoMethods* Methods = CreateMethods();

    internal static string GetName(SqlitePhysicalLimits limits)
    {
        limits.Validate();
        lock (RegistrationGate)
        {
            if (Registrations.TryGetValue(limits, out var name)) return name;
            if (Registrations.Count >= 16) throw new StorageCapacityExceededException();
            SqliteProviderInitializer.EnsureInitialized();
            var parent = Native.Find(null);
            if (parent == null) throw new InvalidOperationException("The SQLite3MC platform VFS is unavailable.");
            // Do not wrap the encryption VFS: its internal rekey I/O must also cross our guard.
            var parentName = Marshal.PtrToStringUTF8((nint)parent->Name)!;
            const string cipherPrefix = "multipleciphers-";
            if (parentName.StartsWith(cipherPrefix, StringComparison.Ordinal))
            {
                var encoded = System.Text.Encoding.UTF8.GetBytes(parentName[cipherPrefix.Length..] + '\0');
                fixed (byte* pointer = encoded) parent = Native.Find(pointer);
            }
            if (parent == null || parent->Version != 3)
                throw new InvalidOperationException("An unsupported SQLite platform VFS was encountered.");
            var platformName = Marshal.PtrToStringUTF8((nint)parent->Name)!;
            if (!platformName.StartsWith("unix", StringComparison.Ordinal) &&
                !platformName.StartsWith("win32", StringComparison.Ordinal))
                throw new InvalidOperationException("The bounded SQLite VFS requires the supported Unix or Windows platform VFS.");
            name = "pml-bounded-" + Registrations.Count;
            var state = new VfsState(parent, limits);
            var handle = GCHandle.Alloc(state);
            var vfs = (Vfs*)NativeMemory.AllocZeroed((nuint)sizeof(Vfs));
            if (vfs == null) { handle.Free(); throw new OutOfMemoryException(); }
            *vfs = *parent;
            vfs->Next = null;
            vfs->Name = (byte*)Marshal.StringToCoTaskMemUTF8(name);
            vfs->AppData = GCHandle.ToIntPtr(handle);
            vfs->FileBytes = checked(parent->FileBytes + sizeof(File));
            vfs->Open = &Open;
            vfs->Delete = &Delete;
            vfs->Access = &Access;
            vfs->FullPath = &FullPath;
            vfs->DlOpen = &DlOpen;
            vfs->DlError = &DlError;
            vfs->DlSym = &DlSym;
            vfs->DlClose = &DlClose;
            vfs->Randomness = &Randomness;
            vfs->Sleep = &Sleep;
            vfs->CurrentTime = &CurrentTime;
            vfs->LastError = &LastError;
            if (vfs->Version >= 2) vfs->CurrentTime64 = &CurrentTime64;
            if (vfs->Version >= 3)
            {
                vfs->SetSystemCall = &SetSystemCall;
                vfs->GetSystemCall = &GetSystemCall;
                vfs->NextSystemCall = &NextSystemCall;
            }
            if (Native.Register(vfs, 0) != Ok)
            {
                Marshal.FreeCoTaskMem((nint)vfs->Name);
                NativeMemory.Free(vfs);
                handle.Free();
                throw new InvalidOperationException("The bounded SQLite VFS could not be registered.");
            }
            // SQLite3MC creates the encryption layer over this named VFS on first open.
            var encryptedName = cipherPrefix + name;
            Registrations.Add(limits, encryptedName);
            return encryptedName;
        }
    }

    private sealed class VfsState(Vfs* parent, SqlitePhysicalLimits limits)
    {
        public readonly Vfs* Parent = parent;
        public readonly SqlitePhysicalLimits Limits = limits;
        public readonly object Gate = new();
        public long TemporaryBytes;
        public int TemporaryFiles;
    }

    private static VfsState State(nint handle) => (VfsState)GCHandle.FromIntPtr(handle).Target!;
    private static Vfs* Parent(Vfs* vfs) => State(vfs->AppData).Parent;
    private static File* Inner(File* file) => file->Inner;
    private static IoMethods* Io(File* file) => file->Inner->Methods;
    private static bool Fits(long limit, long offset, long count) => offset >= 0 && count >= 0 && offset <= limit && count <= limit - offset;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Open(Vfs* vfs, byte* name, File* file, int flags, int* outputFlags)
    {
        *file = default;
        var opened = false;
        var reserved = false;
        try
        {
            var state = State(vfs->AppData);
            file->State = vfs->AppData;
            file->Inner = (File*)((byte*)file + sizeof(File));
            // Unknown/new file classes fail into the finite temporary allowance.
            file->Temporary = (flags & (MainDatabase | MainJournal | Wal)) == 0 || (flags & DeleteOnClose) != 0 ? 1 : 0;
            // Refuse persistent auxiliary classes (for example arbitrary ATTACH super-journals).
            // Temporary allowances require OS-backed delete-on-close, including crash cleanup.
            if (file->Temporary != 0 && (flags & DeleteOnClose) == 0) return Full;
            file->Limit = file->Temporary != 0 ? state.Limits.TemporaryBytes :
                (flags & Wal) != 0 ? state.Limits.EffectiveWalBytes :
                (flags & MainJournal) != 0 ? state.Limits.JournalBytes : state.Limits.MainBytes;
            if (file->Temporary != 0)
            {
                lock (state.Gate)
                {
                    if (state.TemporaryFiles >= StorageCapacityConstants.MaximumTemporaryFiles) return Full;
                    state.TemporaryFiles++;
                    reserved = true;
                }
            }
            var result = state.Parent->Open(state.Parent, name, file->Inner, flags, outputFlags);
            // An unsuccessful xOpen is allowed to leave a live xClose method.
            opened = file->Inner->Methods != null;
            if (result != Ok) return result;
            if (!opened) return IoError;
            long length = 0;
            result = Io(file)->FileSize(Inner(file), &length);
            if (result != Ok) return result;
            // Oversized existing evidence is readable/recoverable. No further extension is
            // permitted, and SQLite alone decides whether a checkpoint/truncation is safe.
            if (file->Temporary != 0)
            {
                lock (state.Gate)
                {
                    if (!Fits(state.Limits.TemporaryBytes, state.TemporaryBytes, length)) return Full;
                    state.TemporaryBytes += length;
                    file->TemporaryCharge = length;
                }
            }
            file->Methods = Methods;
            return Ok;
        }
        catch { return IoError; } // No managed exception may unwind through SQLite.
        finally
        {
            if (file->Methods == null)
            {
                var closed = !opened || Io(file)->Close(Inner(file)) == Ok;
                if (reserved && closed)
                {
                    var state = State(file->State);
                    lock (state.Gate)
                    {
                        state.TemporaryFiles--;
                        state.TemporaryBytes -= file->TemporaryCharge;
                    }
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Close(File* file)
    {
        var result = Io(file)->Close(Inner(file));
        if (file->Temporary != 0)
        {
            var state = State(file->State);
            lock (state.Gate)
            {
                // Failed close must not refund bytes whose retirement is uncertain.
                if (result == Ok)
                {
                    state.TemporaryFiles--;
                    state.TemporaryBytes -= file->TemporaryCharge;
                }
            }
        }
        file->Methods = null;
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Write(File* file, void* data, int count, long offset)
    {
        if (!Fits(file->Limit, offset, count)) return Full;
        if (file->Temporary == 0) return Io(file)->Write(Inner(file), data, count, offset);
        var state = State(file->State);
        lock (state.Gate)
        {
            var growth = Math.Max(0, offset + count - file->TemporaryCharge);
            if (!Fits(state.Limits.TemporaryBytes, state.TemporaryBytes, growth)) return Full;
            // Reserve before I/O, including partial/failed writes. Refunded only by a proven
            // successful truncate/close; a failed write must never undercharge its extension.
            state.TemporaryBytes += growth;
            file->TemporaryCharge += growth;
            return Io(file)->Write(Inner(file), data, count, offset);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Truncate(File* file, long size)
    {
        if (!Fits(file->Limit, 0, size)) return Full;
        if (file->Temporary == 0) return Io(file)->Truncate(Inner(file), size);
        var state = State(file->State);
        lock (state.Gate)
        {
            var growth = Math.Max(0, size - file->TemporaryCharge);
            if (!Fits(state.Limits.TemporaryBytes, state.TemporaryBytes, growth)) return Full;
            state.TemporaryBytes += growth;
            file->TemporaryCharge += growth;
            var result = Io(file)->Truncate(Inner(file), size);
            if (result == Ok)
            {
                state.TemporaryBytes -= file->TemporaryCharge - size;
                file->TemporaryCharge = size;
            }
            return result;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FileControl(File* file, int operation, void* argument)
    {
        // The OS must not preallocate past a limit or round later writes to a chunk size.
        // mmap is disabled: all file extension must pass xWrite/xTruncate.
        if (operation is 5 or 6) return Ok; // SIZE_HINT, CHUNK_SIZE: optional hints only.
        if (operation == 18) { *(long*)argument = 0; return Ok; } // MMAP_SIZE
        return Io(file)->FileControl(Inner(file), operation, argument);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShmMap(File* file, int page, int pageSize, int extend, void** address)
    {
        *address = null;
        var limit = State(file->State).Limits.SharedMemoryBytes;
        if (page < 0 || pageSize <= 0 || !Fits(limit, (long)page * pageSize, pageSize)) return Full;
        return Io(file)->Version >= 2 && Io(file)->ShmMap != null
            ? Io(file)->ShmMap(Inner(file), page, pageSize, extend, address) : IoError;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Fetch(File* file, long offset, int count, void** address) { *address = null; return Ok; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unfetch(File* file, long offset, void* address) => Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(File* file, void* data, int count, long offset) => Io(file)->Read(Inner(file), data, count, offset);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Sync(File* file, int flags) => Io(file)->Sync(Inner(file), flags);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FileSize(File* file, long* size) => Io(file)->FileSize(Inner(file), size);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Lock(File* file, int level) => Io(file)->Lock(Inner(file), level);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unlock(File* file, int level) => Io(file)->Unlock(Inner(file), level);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CheckLock(File* file, int* result) => Io(file)->CheckLock(Inner(file), result);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SectorSize(File* file) => Io(file)->SectorSize(Inner(file));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Characteristics(File* file) => Io(file)->Characteristics(Inner(file));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShmLock(File* file, int offset, int count, int flags) => Io(file)->Version >= 2 && Io(file)->ShmLock != null ? Io(file)->ShmLock(Inner(file), offset, count, flags) : IoError;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ShmBarrier(File* file) { if (Io(file)->Version >= 2 && Io(file)->ShmBarrier != null) Io(file)->ShmBarrier(Inner(file)); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ShmUnmap(File* file, int delete) => Io(file)->Version >= 2 && Io(file)->ShmUnmap != null ? Io(file)->ShmUnmap(Inner(file), delete) : Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Delete(Vfs* vfs, byte* name, int sync) => Parent(vfs)->Delete(Parent(vfs), name, sync);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Access(Vfs* vfs, byte* name, int flags, int* result) => Parent(vfs)->Access(Parent(vfs), name, flags, result);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FullPath(Vfs* vfs, byte* name, int count, byte* output) => Parent(vfs)->FullPath(Parent(vfs), name, count, output);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* DlOpen(Vfs* vfs, byte* name) => Parent(vfs)->DlOpen != null ? Parent(vfs)->DlOpen(Parent(vfs), name) : null;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DlError(Vfs* vfs, int count, byte* output) { if (Parent(vfs)->DlError != null) Parent(vfs)->DlError(Parent(vfs), count, output); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* DlSym(Vfs* vfs, void* module, byte* symbol) => Parent(vfs)->DlSym != null ? Parent(vfs)->DlSym(Parent(vfs), module, symbol) : null;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DlClose(Vfs* vfs, void* module) { if (Parent(vfs)->DlClose != null) Parent(vfs)->DlClose(Parent(vfs), module); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Randomness(Vfs* vfs, int count, byte* output) => Parent(vfs)->Randomness(Parent(vfs), count, output);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Sleep(Vfs* vfs, int microseconds) => Parent(vfs)->Sleep(Parent(vfs), microseconds);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CurrentTime(Vfs* vfs, double* value) => Parent(vfs)->CurrentTime(Parent(vfs), value);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int LastError(Vfs* vfs, int count, byte* output) => Parent(vfs)->LastError != null ? Parent(vfs)->LastError(Parent(vfs), count, output) : 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CurrentTime64(Vfs* vfs, long* value)
    {
        var parent = Parent(vfs);
        if (parent->CurrentTime64 != null) return parent->CurrentTime64(parent, value);
        double days = 0;
        var result = parent->CurrentTime(parent, &days);
        *value = (long)(days * 86400000);
        return result;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SetSystemCall(Vfs* vfs, byte* name, void* call) => Parent(vfs)->SetSystemCall != null ? Parent(vfs)->SetSystemCall(Parent(vfs), name, call) : NotFound;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* GetSystemCall(Vfs* vfs, byte* name) => Parent(vfs)->GetSystemCall != null ? Parent(vfs)->GetSystemCall(Parent(vfs), name) : null;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* NextSystemCall(Vfs* vfs, byte* name) => Parent(vfs)->NextSystemCall != null ? Parent(vfs)->NextSystemCall(Parent(vfs), name) : null;

    private static IoMethods* CreateMethods()
    {
        var methods = (IoMethods*)NativeMemory.AllocZeroed((nuint)sizeof(IoMethods));
        if (methods == null) throw new OutOfMemoryException();
        *methods = new IoMethods
        {
            Version = 3, Close = &Close, Read = &Read, Write = &Write, Truncate = &Truncate,
            Sync = &Sync, FileSize = &FileSize, Lock = &Lock, Unlock = &Unlock, CheckLock = &CheckLock,
            FileControl = &FileControl, SectorSize = &SectorSize, Characteristics = &Characteristics,
            ShmMap = &ShmMap, ShmLock = &ShmLock, ShmBarrier = &ShmBarrier, ShmUnmap = &ShmUnmap,
            Fetch = &Fetch, Unfetch = &Unfetch
        };
        return methods;
    }

    // Sequential layouts mirror sqlite3.h. Pointer fields use native alignment on x86/x64/ARM.
    [StructLayout(LayoutKind.Sequential)]
    private struct File
    {
        public IoMethods* Methods;
        public File* Inner;
        public long Limit, TemporaryCharge;
        public nint State;
        public int Temporary;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoMethods
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<File*, int> Close;
        public delegate* unmanaged[Cdecl]<File*, void*, int, long, int> Read, Write;
        public delegate* unmanaged[Cdecl]<File*, long, int> Truncate;
        public delegate* unmanaged[Cdecl]<File*, int, int> Sync;
        public delegate* unmanaged[Cdecl]<File*, long*, int> FileSize;
        public delegate* unmanaged[Cdecl]<File*, int, int> Lock, Unlock;
        public delegate* unmanaged[Cdecl]<File*, int*, int> CheckLock;
        public delegate* unmanaged[Cdecl]<File*, int, void*, int> FileControl;
        public delegate* unmanaged[Cdecl]<File*, int> SectorSize, Characteristics;
        public delegate* unmanaged[Cdecl]<File*, int, int, int, void**, int> ShmMap;
        public delegate* unmanaged[Cdecl]<File*, int, int, int, int> ShmLock;
        public delegate* unmanaged[Cdecl]<File*, void> ShmBarrier;
        public delegate* unmanaged[Cdecl]<File*, int, int> ShmUnmap;
        public delegate* unmanaged[Cdecl]<File*, long, int, void**, int> Fetch;
        public delegate* unmanaged[Cdecl]<File*, long, void*, int> Unfetch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vfs
    {
        public int Version, FileBytes, MaximumPath;
        public Vfs* Next;
        public byte* Name;
        public nint AppData;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, File*, int, int*, int> Open;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, int, int> Delete;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, int, int*, int> Access;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, int, byte*, int> FullPath;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, void*> DlOpen;
        public delegate* unmanaged[Cdecl]<Vfs*, int, byte*, void> DlError;
        public delegate* unmanaged[Cdecl]<Vfs*, void*, byte*, void*> DlSym;
        public delegate* unmanaged[Cdecl]<Vfs*, void*, void> DlClose;
        public delegate* unmanaged[Cdecl]<Vfs*, int, byte*, int> Randomness;
        public delegate* unmanaged[Cdecl]<Vfs*, int, int> Sleep;
        public delegate* unmanaged[Cdecl]<Vfs*, double*, int> CurrentTime;
        public delegate* unmanaged[Cdecl]<Vfs*, int, byte*, int> LastError;
        public delegate* unmanaged[Cdecl]<Vfs*, long*, int> CurrentTime64;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, void*, int> SetSystemCall;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, void*> GetSystemCall;
        public delegate* unmanaged[Cdecl]<Vfs*, byte*, byte*> NextSystemCall;
    }

    private static class Native
    {
        // The same sqlite3mc binary used by SQLite3MC.PCLRaw.provider; never load system SQLite.
        [DllImport("sqlite3mc", EntryPoint = "sqlite3_vfs_find", CallingConvention = CallingConvention.Cdecl)]
        internal static extern Vfs* Find(byte* name);
        [DllImport("sqlite3mc", EntryPoint = "sqlite3_vfs_register", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Register(Vfs* vfs, int makeDefault);
    }
}
