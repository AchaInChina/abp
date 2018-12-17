﻿using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Threading;

namespace Volo.Abp.RabbitMQ
{
    public class RabbitMqMessageConsumer : IRabbitMqMessageConsumer, ITransientDependency, IDisposable
    {
        public ILogger<RabbitMqMessageConsumer> Logger { get; set; }

        protected IConnectionPool ConnectionPool { get; }

        protected AbpTimer Timer { get; }

        protected ExchangeDeclareConfiguration Exchange { get; private set; }

        protected QueueDeclareConfiguration Queue { get; private set; }

        protected string ConnectionName { get; private set; }

        protected ConcurrentBag<Func<IModel, BasicDeliverEventArgs, Task>> Callbacks { get; }

        protected IModel Channel { get; private set; }

        public RabbitMqMessageConsumer(
            IConnectionPool connectionPool,
            AbpTimer timer)
        {
            ConnectionPool = connectionPool;
            Timer = timer;
            Logger = NullLogger<RabbitMqMessageConsumer>.Instance;

            Callbacks = new ConcurrentBag<Func<IModel, BasicDeliverEventArgs, Task>>();

            Timer.Period = 5000; //5 sec.
            Timer.Elapsed += Timer_Elapsed;
        }

        public void Initialize(
            [NotNull] ExchangeDeclareConfiguration exchange,
            [NotNull] QueueDeclareConfiguration queue,
            string connectionName = null)
        {
            Exchange = Check.NotNull(exchange, nameof(exchange));
            Queue = Check.NotNull(queue, nameof(queue));
            ConnectionName = connectionName;
        }

        public void OnMessageReceived(Func<IModel, BasicDeliverEventArgs, Task> callback)
        {
            Callbacks.Add(callback);
        }

        protected virtual void Timer_Elapsed(object sender, EventArgs e)
        {
            if (Channel == null || Channel.IsOpen == false)
            {
                TryCreateChannel();
            }
        }

        private void TryCreateChannel()
        {
            DisposeChannel();

            try
            {
                var channel = ConnectionPool
                    .Get(ConnectionName)
                    .CreateModel();

                channel.ExchangeDeclare(
                    exchange: Exchange.ExchangeName,
                    type: Exchange.Type
                );

                channel.QueueDeclare(
                    queue: Queue.QueueName,
                    durable: Queue.Durable,
                    exclusive: Queue.Exclusive,
                    autoDelete: Queue.AutoDelete,
                    arguments: Queue.Arguments
                );

                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += async (model, basicDeliverEventArgs) =>
                {
                    await HandleIncomingMessage(channel, basicDeliverEventArgs);
                };

                channel.BasicConsume(
                    queue: Queue.QueueName,
                    autoAck: false,
                    consumer: consumer
                );

                Channel = channel;
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogLevel.Warning);
            }
        }

        protected virtual async Task HandleIncomingMessage(IModel channel, BasicDeliverEventArgs basicDeliverEventArgs)
        {
            try
            {
                foreach (var callback in Callbacks)
                {
                    await callback(channel, basicDeliverEventArgs);
                }

                channel.BasicAck(basicDeliverEventArgs.DeliveryTag, multiple: false);
            }
            catch (Exception ex)
            {
                Logger.LogException(ex);
            }
        }

        protected virtual void DisposeChannel()
        {
            if (Channel == null)
            {
                return;
            }

            try
            {
                Channel.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, LogLevel.Warning);
            }
        }

        public virtual void Dispose()
        {
            DisposeChannel();
        }
    }
}