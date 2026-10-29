// Stand-ins for the Dataverse SDK types, so this test assembly contains plug-in classes for
// PluginClasses to find. Only their full names matter.

namespace Microsoft.Xrm.Sdk
{
    public interface IPlugin
    {
        void Execute(System.IServiceProvider serviceProvider);
    }
}

namespace System.Activities
{
    public abstract class CodeActivity;
}

namespace GitPa.Tests.FakePlugins
{
    using Microsoft.Xrm.Sdk;

    public abstract class BasePlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider) { }
    }

    public class AccountPlugin : BasePlugin;

    public class ContactPlugin : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider) { }
    }

    public interface ICustomPlugin : IPlugin;

    public class ViaInterfacePlugin : ICustomPlugin
    {
        public void Execute(IServiceProvider serviceProvider) { }
    }

    public class SendEmailActivity : System.Activities.CodeActivity;

    public class Outer
    {
        public class NestedPlugin : BasePlugin;
    }

    internal class InternalPlugin : BasePlugin;

    public class NotAPlugin;
}
