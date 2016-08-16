using Microsoft.Owin;
using Owin;

[assembly: OwinStartupAttribute(typeof(dumpling.web.Startup))]
namespace dumpling.web
{
    public partial class Startup
    {
        public void Configuration(IAppBuilder app)
        {
        }
    }
}
