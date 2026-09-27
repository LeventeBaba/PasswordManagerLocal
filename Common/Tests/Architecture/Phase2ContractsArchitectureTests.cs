using Microsoft.VisualStudio.TestTools.UnitTesting;
using PasswordManagerLocal.Common.Contracts.Endpoints;
using PasswordManagerLocal.Common.Contracts.Notifications;
using PasswordManagerLocal.Common.Contracts.Requests;
using PasswordManagerLocal.Common.Contracts.Serialization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;

namespace PasswordManagerLocal.Common.Tests.Architecture;

[TestClass]
public sealed class Phase2ContractsArchitectureTests
{
    [TestMethod]
    public void ContractsAssemblyHasNoForbiddenImplementationReferences()
    {
        var forbiddenPrefixes = new[]
        {
            "Avalonia", "ReactiveUI", "Microsoft.EntityFrameworkCore",
            "PasswordManagerLocal.Common.Backend", "PasswordManagerLocal.Windows",
            "Mono.Android", "System.Windows.Forms"
        };

        var references = typeof(IEndpoints).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        foreach (var forbiddenPrefix in forbiddenPrefixes)
            Assert.IsFalse(references.Any(reference => reference.StartsWith(forbiddenPrefix, StringComparison.Ordinal)),
                $"Contracts references forbidden assembly prefix '{forbiddenPrefix}'.");
    }

    [TestMethod]
    public void EndpointSignaturesOnlyExposeContractsOrFrameworkTypes()
    {
        var contractsAssembly = typeof(IEndpoints).Assembly;
        foreach (var method in typeof(IEndpoints).GetMethods())
        {
            AssertContractClosure(method.ReturnType, contractsAssembly, method.Name);
            foreach (var parameter in method.GetParameters())
                AssertContractClosure(parameter.ParameterType, contractsAssembly, method.Name);
        }
    }

    [TestMethod]
    public void FrontendReferencesContractsWithoutBackendImplementationAssemblies()
    {
        var projectPath = Path.Combine(
            GetRepositoryRoot(),
            "Common",
            "Frontend",
            "PasswordManagerLocal.Common.Frontend.csproj");
        var references = XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        Assert.IsTrue(references.Any(reference => reference.EndsWith(
            "PasswordManagerLocal.Common.Contracts.csproj",
            StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(references.Any(reference => reference.EndsWith(
            "PasswordManagerLocal.Common.Backend.csproj",
            StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(references.Any(reference => reference.Contains(
            "Runtime.Abstractions",
            StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void ObsoleteRuntimeAbstractionsProjectIsRemoved()
    {
        var root = GetRepositoryRoot();
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "PasswordManagerLocal.Runtime.Abstractions")));
        Assert.IsFalse(File.ReadAllText(Path.Combine(root, "PasswordManagerLocal.sln"))
            .Contains("PasswordManagerLocal.Runtime.Abstractions", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EndpointContractSerializationMetadataRoundTripsMovedDto()
    {
        var source = new LoginRequest
        {
            Username = "phase2-contract",
            Password = [1, 2, 3, 4],
            RememberMe = true
        };

        var json = JsonSerializer.Serialize(source, EndpointContractsJsonContext.Default.LoginRequest);
        var roundTrip = JsonSerializer.Deserialize(json, EndpointContractsJsonContext.Default.LoginRequest);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(source.Username, roundTrip.Username);
        CollectionAssert.AreEqual(source.Password, roundTrip.Password);
        Assert.AreEqual(source.RememberMe, roundTrip.RememberMe);
    }

    [TestMethod]
    public void NotificationContractContainsMetadataOnly()
    {
        var properties = typeof(FrontendChangeNotification).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "OccurredAtUtc", "RuntimeInstanceId", "Scope", "Sequence", "UserId" },
            properties);
    }

    [TestMethod]
    public void NotificationCompositeFlagsMatchPublishedScopes()
    {
        Assert.AreEqual(FrontendChangeScope.UserProfile | FrontendChangeScope.Passwords | FrontendChangeScope.Devices,
            FrontendChangeScope.AllAuthenticatedData);
        Assert.AreEqual((FrontendChangeScope)255, FrontendChangeScope.Everything);
    }

    private static void AssertContractClosure(Type type, Assembly contractsAssembly, string methodName)
    {
        if (type.IsByRef || type.IsArray)
        {
            AssertContractClosure(type.GetElementType()!, contractsAssembly, methodName);
            return;
        }

        if (type.IsGenericType)
        {
            AssertAllowedAssembly(type.GetGenericTypeDefinition(), contractsAssembly, methodName);
            foreach (var argument in type.GetGenericArguments())
                AssertContractClosure(argument, contractsAssembly, methodName);
            return;
        }

        AssertAllowedAssembly(type, contractsAssembly, methodName);
    }

    private static void AssertAllowedAssembly(Type type, Assembly contractsAssembly, string methodName)
    {
        if (type == typeof(void) || type.Assembly == contractsAssembly ||
            type.Assembly == typeof(string).Assembly)
            return;

        Assert.Fail($"Endpoint method '{methodName}' exposes non-contract type '{type.FullName}'.");
    }

    private static string GetRepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "..", ".."));
}
