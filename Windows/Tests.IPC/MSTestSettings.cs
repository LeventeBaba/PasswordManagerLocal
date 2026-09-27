using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace PasswordManagerLocal.Windows.Tests.IPC;

internal static class MSTestSettings
{
}
