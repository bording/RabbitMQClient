﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RabbitMQClient
{
    public class ConnectionFactory
    {
        public string HostName { get; set; }

        public string UserName { get; set; } = "guest";

        public string Password { get; set; } = "guest";

        public string VirtualHost { get; set; } = "/";

        public async Task<Connection> CreateConnection()
        {
            var connection = new Connection(HostName, UserName, Password, VirtualHost);
            await connection.Connect();

            return connection;
        }
    }
}