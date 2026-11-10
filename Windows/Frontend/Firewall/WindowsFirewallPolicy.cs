using System.Runtime.InteropServices;

namespace PasswordManagerLocal.Windows.Frontend.Firewall;

// Direct calls to the SDK's IDispatch-derived vtables avoid dynamic COM/reflection
// and runtime-generated COM stubs in the trimmed Windows publication. The slot
// numbers below include IUnknown (0..2) and IDispatch (3..6), per netfw.h.
internal sealed class WindowsFirewallPolicy : IWindowsFirewallPolicy
{
    private static readonly Guid PolicyClass = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    private static readonly Guid PolicyInterface = new("98325047-C671-4174-8D81-DEFCD3F03186");
    private static readonly Guid RuleClass = new("2C5BC43E-3369-4C33-AB0C-BE9469677AF4");
    private static readonly Guid RuleInterface = new("AF230D27-BABA-4E42-ACED-F524F22CFCE2");
    private static readonly Guid EnumInterface = new("00020404-0000-0000-C000-000000000046");
    private readonly NativeObject _policy;
    private readonly NativeObject _rules;
    private readonly bool _uninitialize;
    private bool _disposed;

    internal WindowsFirewallPolicy()
    {
        var initialized = CoInitializeEx(0, 0); // MTA; an existing STA is also supported.
        if (initialized != unchecked((int)0x80010106)) // RPC_E_CHANGED_MODE
        {
            Marshal.ThrowExceptionForHR(initialized);
            _uninitialize = true; // Both S_OK and S_FALSE require CoUninitialize.
        }
        NativeObject? policy = null;
        try
        {
            policy = NativeObject.Create(PolicyClass, PolicyInterface);
            _rules = policy.GetObject(18); // INetFwPolicy2.Rules
            _policy = policy;
        }
        catch
        {
            policy?.Dispose();
            if (_uninitialize)
                CoUninitialize();
            throw;
        }
    }

    public WindowsFirewallSnapshot Read()
    {
        var profiles = new[] { 1, 2, 4 }.Select(type => new WindowsFirewallProfile(
            type, _policy.GetProfileBoolean(8, type), _policy.GetProfileBoolean(12, type),
            _policy.GetProfileInteger(25, type))).ToArray();
        return new(_policy.GetInteger(7), _policy.GetInteger(28), profiles, ReadRules());
    }

    public void Add(WindowsFirewallRule rule)
    {
        using var native = NativeObject.Create(RuleClass, RuleInterface);
        native.SetString(8, rule.Name);
        native.SetString(10, "Password Manager Local Demo Agent synchronization and device discovery.");
        native.SetString(12, rule.Program);
        native.SetInteger(16, rule.Protocol); // Protocol must precede port properties.
        native.SetString(18, rule.LocalPorts);
        native.SetString(20, rule.RemotePorts);
        native.SetString(22, rule.LocalAddresses);
        native.SetString(24, rule.RemoteAddresses); // Includes multicast destinations.
        native.SetInteger(28, rule.Direction);
        native.SetString(32, rule.InterfaceTypes);
        native.SetString(36, rule.Grouping);
        native.SetInteger(38, rule.Profiles);
        native.SetBoolean(40, rule.EdgeTraversal);
        native.SetInteger(42, rule.Action);
        native.SetBoolean(34, rule.Enabled);
        _rules.Add(native);
    }

    public void Remove(string name) => _rules.Remove(name);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rules.Dispose();
        _policy.Dispose();
        if (_uninitialize)
            CoUninitialize();
    }

    private unsafe IReadOnlyList<WindowsFirewallRule> ReadRules()
    {
        var result = new List<WindowsFirewallRule>();
        using var unknown = _rules.GetObject(11); // INetFwRules._NewEnum
        using var enumerator = NativeObject.Query(unknown.Pointer, EnumInterface);
        // VARIANT is 24 bytes on 64-bit and 16 on 32-bit; this buffer fits both.
        byte* variant = stackalloc byte[24];
        while (true)
        {
            new Span<byte>(variant, 24).Clear();
            uint fetched = 0;
            try
            {
                var next = (delegate* unmanaged[Stdcall]<nint, uint, void*, uint*, int>)enumerator.Method(3);
                Marshal.ThrowExceptionForHR(next(enumerator.Pointer, 1, variant, &fetched));
                if (fetched == 0)
                    break;
                var type = *(ushort*)variant;
                if (type is not (9 or 13)) // VT_DISPATCH / VT_UNKNOWN
                    throw new COMException("Unexpected Windows Firewall rule enumerator value.");
                using var rule = NativeObject.Query(*(nint*)(variant + 8), RuleInterface);
                var protocol = rule.GetInteger(15);
                result.Add(new WindowsFirewallRule(
                    rule.GetString(7), rule.GetString(35), rule.GetString(11), protocol,
                    rule.GetInteger(27), rule.GetInteger(41), rule.GetBoolean(33), rule.GetInteger(37),
                    protocol is 6 or 17 ? rule.GetString(17) : "*",
                    protocol is 6 or 17 ? rule.GetString(19) : "*",
                    rule.GetString(23), rule.GetString(21), rule.GetString(13),
                    rule.GetString(31), rule.GetBoolean(39)));
            }
            finally
            {
                VariantClear((nint)variant); // Releases the enumerator's rule reference.
            }
        }
        return result;
    }

    private sealed unsafe class NativeObject(nint pointer) : IDisposable
    {
        internal nint Pointer { get; private set; } = pointer;
        internal nint Method(int slot) => (*(nint**)Pointer)[slot];

        internal static NativeObject Create(Guid classId, Guid interfaceId)
        {
            Marshal.ThrowExceptionForHR(CoCreateInstance(in classId, 0, 1, in interfaceId, out var value));
            return new(value);
        }

        internal static NativeObject Query(nint value, Guid interfaceId)
        {
            nint queried = 0;
            var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)value)[0];
            Marshal.ThrowExceptionForHR(query(value, &interfaceId, &queried));
            return new(queried);
        }

        internal int GetInteger(int slot)
        {
            var value = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)Method(slot))(Pointer, &value));
            return value;
        }

        internal int GetProfileInteger(int slot, int profile)
        {
            var value = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int*, int>)Method(slot))(Pointer, profile, &value));
            return value;
        }

        internal bool GetBoolean(int slot)
        {
            short value = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, short*, int>)Method(slot))(Pointer, &value));
            return value != 0;
        }

        internal bool GetProfileBoolean(int slot, int profile)
        {
            short value = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, short*, int>)Method(slot))(Pointer, profile, &value));
            return value != 0;
        }

        internal string GetString(int slot)
        {
            nint value = 0;
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(slot))(Pointer, &value));
                return value == 0 ? string.Empty : Marshal.PtrToStringBSTR(value);
            }
            finally
            {
                if (value != 0)
                    Marshal.FreeBSTR(value);
            }
        }

        internal NativeObject GetObject(int slot)
        {
            nint value = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(slot))(Pointer, &value));
            return new(value);
        }

        internal void SetString(int slot, string value)
        {
            var bstr = Marshal.StringToBSTR(value);
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(slot))(Pointer, bstr));
            }
            finally { Marshal.FreeBSTR(bstr); }
        }

        internal void SetInteger(int slot, int value) =>
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)Method(slot))(Pointer, value));

        internal void SetBoolean(int slot, bool value) =>
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, short, int>)Method(slot))(Pointer, value ? (short)-1 : (short)0));

        internal void Add(NativeObject rule) =>
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(8))(Pointer, rule.Pointer));

        internal void Remove(string name) => SetString(9, name);

        public void Dispose()
        {
            if (Pointer == 0)
                return;
            ((delegate* unmanaged[Stdcall]<nint, uint>)Method(2))(Pointer);
            Pointer = 0;
        }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint value);

    [DllImport("oleaut32.dll", ExactSpelling = true)]
    private static extern int VariantClear(nint variant);
}
