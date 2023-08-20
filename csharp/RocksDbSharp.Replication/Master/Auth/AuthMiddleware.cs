using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RocksDbSharp.Replication.Master.Auth
{
    public class AuthMiddleware
    {
        private readonly RequestDelegate _next;
        private ReplicatedDbMaster _replicationMaster;

        public AuthMiddleware(RequestDelegate next, ReplicatedDbMaster replicationMaster)
        {
            _replicationMaster = replicationMaster;
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Headers.TryGetValue("AuthKey", out var headerVal) || headerVal.FirstOrDefault() != _replicationMaster.AuthKey)
            {
                context.Response.StatusCode = 403;
                return;
            }

            // Call the next delegate/middleware in the pipeline.
            await _next(context);
        }
    }
}
