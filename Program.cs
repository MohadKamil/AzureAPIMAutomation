using System.Threading.Tasks;
using Pulumi;

namespace AzureAPIMAutomation
{
    internal static class Program
    {
        static Task<int> Main() => Deployment.RunAsync<ApiStack>();
    }
}
