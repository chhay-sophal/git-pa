namespace GitPa.Tests;

public class PluginClassesTests
{
    public static readonly string[] FakePluginClasses =
    [
        "GitPa.Tests.FakePlugins.AccountPlugin",
        "GitPa.Tests.FakePlugins.ContactPlugin",
        "GitPa.Tests.FakePlugins.Outer+NestedPlugin",
        "GitPa.Tests.FakePlugins.SendEmailActivity",
        "GitPa.Tests.FakePlugins.ViaInterfacePlugin",
    ];

    [Fact]
    public void FindsPublicConcretePluginAndWorkflowActivityClasses()
    {
        var classes = PluginClasses.Read(typeof(PluginClassesTests).Assembly.Location)
            .Where(c => c.StartsWith("GitPa.Tests.FakePlugins."));

        Assert.Equal(FakePluginClasses, classes);
    }
}
