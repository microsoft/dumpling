using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Hosting;

namespace dumpling.web.Storage
{
    public static class TaskHostExtensions
    {
        public static Task RegisterWithHost(this Task task)
        {
            var host = new TaskHost(task);

            return task;
        }

        private class TaskHost : IRegisteredObject
        {
            private Task _task;
            private ManualResetEventSlim _completed = new ManualResetEventSlim(false);

            public TaskHost(Task task)
            {
                _task = task;

                _task.ContinueWith(t => { HostingEnvironment.UnregisterObject(this); _completed.Set(); });

                HostingEnvironment.RegisterObject(this);
            }

            public void Stop(bool immediate)
            {
                _completed.Wait();
            }
        }
    }
}