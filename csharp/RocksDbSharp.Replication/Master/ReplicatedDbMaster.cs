using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Mvc;
using RocksDbSharp.Replication.Master.Controllers;
using RocksDbSharp.Replication.Master.Auth;
using System.Net.Sockets;
using System.Net;

namespace RocksDbSharp.Replication.Master
{
    public class ReplicatedDbMaster
    {
        public RocksDb DB { get; set; }
        public int ControlPort { get; set; }
        public int ServicePort { get; set; }
        public string AuthKey { get; set; }
        public OptionsHandle DbOptions { get; private set; }
        private RocksDBReplicationServiceServer ServiceServer { get; set; }
        public Dictionary<string, SessionRequest> SessionRequests { get; set; } = new Dictionary<string, SessionRequest>();

        public ReplicatedDbMaster(OptionsHandle options, string path, int controlPort, int servicePort, string authKey)
        {
            DbOptions = options;
            DB = RocksDb.Open(options, path);

            ControlPort = controlPort;
            ServicePort = servicePort;
            AuthKey = authKey;
            ServiceServer = new RocksDBReplicationServiceServer(this, IPAddress.Any, servicePort);
        }

        public async Task RunAsync()
        {
            var apiThread = new Thread(RunControlApi);
            apiThread.IsBackground = true;
            apiThread.Start();

            ServiceServer.RunAsync();
        }


        private void RunControlApi()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddControllers().AddApplicationPart(typeof(SessionController).Assembly);
            builder.Services.AddSingleton(typeof(ReplicatedDbMaster), this);
            builder.Services.Configure<KestrelServerOptions>(options =>
            {
                options.AllowSynchronousIO = true;
            });

            var app = builder.Build();
            app.UseMiddleware<AuthMiddleware>();
            app.MapControllers();
            app.Run($"http://*:{ControlPort}");
        }
    }
}
