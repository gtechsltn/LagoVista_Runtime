﻿using LagoVista.Core;
using LagoVista.Core.Validation;
using LagoVista.IoT.Deployment.Admin.Models;
using LagoVista.IoT.Pipeline.Admin.Models;
using LagoVista.IoT.Runtime.Core.Models.PEM;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LagoVista.IoT.Runtime.Core.Module
{
    public abstract class ListenerModule : PipelineModule
    {
        ListenerConfiguration _listenerConfiguration;
        IPEMQueue _outgoingMessageQueue;

        public ListenerModule(ListenerConfiguration listenerConfiguration, IPEMBus pemBus) : base(listenerConfiguration, pemBus)
        {
            _listenerConfiguration = listenerConfiguration;

            _outgoingMessageQueue = pemBus.Queues.Where(queue => queue.PipelineModuleId == listenerConfiguration.Id).FirstOrDefault();
            if (_outgoingMessageQueue == null) throw new Exception($"Incoming queue for listener module {_listenerConfiguration.Id} - {_listenerConfiguration.Name} could not be found.");
        }

        public async Task<InvokeResult> AddBinaryMessageAsync(byte[] buffer, DateTime startTimeStamp, String deviceId = "", String topic = "")
        {
            try
            {
                var message = new PipelineExecutionMessage()
                {
                    PayloadType = MessagePayloadTypes.Binary,
                    BinaryPayload = buffer,
                    CreationTimeStamp = startTimeStamp.ToJSONString()
                };

                Metrics.MessagesProcessed++;

                if (buffer != null)
                {
                    message.PayloadLength = buffer.Length;
                }

                Metrics.BytesProcessed = message.PayloadLength + (String.IsNullOrEmpty(topic) ? 0 : topic.Length);

                message.Envelope.DeviceId = deviceId;
                message.Envelope.Topic = topic;

                var listenerInstruction = new PipelineExecutionInstruction()
                {
                    Name = _listenerConfiguration.Name,
                    Type = GetType().Name,
                    QueueId = "N/A",
                    StartDateStamp = startTimeStamp.ToJSONString(),
                    ProcessByHostId = PEMBus.Instance.PrimaryHost.Id,
                    ExecutionTimeMS = (DateTime.UtcNow - startTimeStamp).TotalMilliseconds,
                };

                message.Instructions.Add(listenerInstruction);

                var planner = PEMBus.Instance.Solution.Value.Planner.Value;
                var plannerInstruction = new PipelineExecutionInstruction()
                {
                    Name = "Planner",
                    Type = "Planner",
                    QueueId = "N/A",
                };

                message.CurrentInstruction = plannerInstruction;
                message.Instructions.Add(plannerInstruction);

                var plannerQueue = PEMBus.Queues.Where(queue => queue.ForModuleType == PipelineModuleType.Planner).FirstOrDefault();
                await plannerQueue.EnqueueAsync(message);

                return InvokeResult.Success;
            }
            catch (Exception ex)
            {
                PEMBus.InstanceLogger.AddException("ListenerModule_AddBinaryMessageAsync", ex);
                return InvokeResult.FromException("ListenerModule_AddBinaryMessageAsync", ex);
            }
        }

        public async Task<InvokeResult> AddMediaMessageAsync(Stream stream, string contentType, long contentLength, DateTime startTimeStamp, string path, String deviceId = "", String topic = "", Dictionary<string, string> headers = null)
        {
            try
            {
                var message = new PipelineExecutionMessage()
                {
                    PayloadType = MessagePayloadTypes.Media,
                    CreationTimeStamp = startTimeStamp.ToJSONString()
                };

                Metrics.MessagesProcessed++;

                message.PayloadLength = contentLength;
                Metrics.BytesProcessed += message.PayloadLength + (String.IsNullOrEmpty(topic) ? 0 : topic.Length);

                message.Envelope.DeviceId = deviceId;
                message.Envelope.Topic = topic;
                message.Envelope.Path = path;

                var headerLength = 0;

                if (headers != null)
                {
                    if (headers.ContainsKey("method"))
                    {
                        message.Envelope.Method = headers["method"];
                    }

                    if (headers.ContainsKey("topic"))
                    {
                        message.Envelope.Topic = headers["topic"];

                        foreach (var header in headers)
                        {
                            headerLength += header.Key.Length + (String.IsNullOrEmpty(header.Value) ? 0 : header.Value.Length);
                        }
                    }

                    if (headers != null)
                    {
                        foreach (var hdr in headers)
                        {
                            message.Envelope.Headers.Add(hdr.Key, hdr.Value);
                        }
                    }
                }

                var listenerInstruction = new PipelineExecutionInstruction()
                {
                    Name = _listenerConfiguration.Name,
                    Type = GetType().Name,
                    QueueId = "N/A",
                    StartDateStamp = startTimeStamp.ToJSONString(),
                    ProcessByHostId = PEMBus.Instance.PrimaryHost.Id,
                    ExecutionTimeMS = (DateTime.UtcNow - startTimeStamp).TotalMilliseconds,
                };

                message.Instructions.Add(listenerInstruction);

                var planner = PEMBus.Instance.Solution.Value.Planner.Value;
                var plannerInstruction = new PipelineExecutionInstruction()
                {
                    Name = "Planner",
                    Type = "Planner",
                    QueueId = "N/A",
                };

                message.CurrentInstruction = plannerInstruction;
                message.Instructions.Add(plannerInstruction);

                var insertResult = await PEMBus.DeviceMediaStorage.StoreMediaItemAsync(stream, message.Id, contentType, contentLength);
                if (!insertResult.Successful)
                {
                    return insertResult.ToInvokeResult();
                }

                message.MediaItemId = insertResult.Result;

                var plannerQueue = PEMBus.Queues.Where(queue => queue.ForModuleType == PipelineModuleType.Planner).FirstOrDefault();
                await plannerQueue.EnqueueAsync(message);

                return InvokeResult.Success;

            }
            catch (Exception ex)
            {
                PEMBus.InstanceLogger.AddException("ListenerModule_AddBinaryMessageAsync", ex);
                return InvokeResult.FromException("ListenerModule_AddBinaryMessageAsync", ex);
            }
        }


        protected void WorkLoop()
        {
            Task.Run(async () =>
            {
                await _outgoingMessageQueue.StartListeningAsync();

                while (Status == PipelineModuleStatus.Running || Status == PipelineModuleStatus.Listening)
                {
                    var msg = await _outgoingMessageQueue.ReceiveAsync();
                    /* queue will return a null message when it's "turned off", should probably change the logic to use cancellation tokens, not today though KDW 5/3/2017 */
                    //TODO Use cancellation token rather than return null when queue is no longer listenting.
                    if (msg != null)
                    {
                        await SendResponseAsync(msg, msg.OutgoingMessages.First());
                    }
                }
            });
        }

        public async override Task<InvokeResult> StartAsync()
        {
            if (_listenerConfiguration.RESTListenerType != RESTListenerTypes.AcmeListener)
            {
                var result = await base.StartAsync();
                if (result.Successful)
                {
                    WorkLoop();
                }

                return result;
            }
            else
            {
                /* ACME Listeners don't participate in the pipline and thus we don't start and stop the work loop in the base class */
                return InvokeResult.Success;
            }
        }

        public async override Task<InvokeResult> StopAsync()
        {
            if (_listenerConfiguration.RESTListenerType != RESTListenerTypes.AcmeListener)
            {
                return await base.StopAsync();
            }
            else
            {
                /* ACME Listeners don't participate in the pipline and thus we don't start and stop the work loop in the base class */
                return InvokeResult.Success;
            }
        }

        public abstract Task<InvokeResult> SendResponseAsync(PipelineExecutionMessage message, OutgoingMessage msg);

        public async Task<InvokeResult> AddStringMessageAsync(string buffer, DateTime startTimeStamp, string path = "", string deviceId = "", string topic = "", Dictionary<string, string> headers = null)
        {
            try
            {
                var message = new PipelineExecutionMessage()
                {
                    PayloadType = MessagePayloadTypes.Text,
                    TextPayload = buffer,
                    CreationTimeStamp = startTimeStamp.ToJSONString()
                };

                var headerLength = 0;

                if (headers != null)
                {
                    if (headers.ContainsKey("method"))
                    {
                        message.Envelope.Method = headers["method"];
                    }

                    if (headers.ContainsKey("topic"))
                    {
                        message.Envelope.Topic = headers["topic"];

                        foreach (var header in headers)
                        {
                            headerLength += header.Key.Length + (String.IsNullOrEmpty(header.Value) ? 0 : header.Value.Length);
                        }
                    }

                    if (headers != null)
                    {
                        foreach (var hdr in headers)
                        {
                            message.Envelope.Headers.Add(hdr.Key, hdr.Value);
                        }
                    }
                }

                message.PayloadLength = String.IsNullOrEmpty(buffer) ? 0 : buffer.Length;

                var bytesProcessed = message.PayloadLength + (String.IsNullOrEmpty(path) ? 0 : path.Length) + headerLength;

                Metrics.BytesProcessed += bytesProcessed;
                Metrics.MessagesProcessed++;

                var json = JsonConvert.SerializeObject(Metrics);
                /*
                Console.WriteLine("LISTENER => " + Id);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(json);
                Console.WriteLine("----------------------------");
                */

                message.Envelope.DeviceId = deviceId;
                message.Envelope.Path = path;
                message.Envelope.Topic = topic;

                var listenerInstruction = new PipelineExecutionInstruction()
                {
                    Name = _listenerConfiguration.Name,
                    Type = GetType().Name,
                    QueueId = _listenerConfiguration.Id,
                    StartDateStamp = startTimeStamp.ToJSONString(),
                    ProcessByHostId = PEMBus.Instance.PrimaryHost.Id,
                    Enqueued = startTimeStamp.ToJSONString(),
                    ExecutionTimeMS = (DateTime.UtcNow - startTimeStamp).TotalMilliseconds,
                };

                message.Instructions.Add(listenerInstruction);

                var plannerQueue = PEMBus.Queues.Where(queue => queue.ForModuleType == PipelineModuleType.Planner).FirstOrDefault();

                if(plannerQueue == null)
                {
                    PEMBus.InstanceLogger.AddError("ListenerModule_AddStringMessageAsync", "Could not find planner queue.");
                    return InvokeResult.FromError("Could not find planner queue.");
                }

                var planner = PEMBus.Instance.Solution.Value.Planner.Value;
                var plannerInstruction = new PipelineExecutionInstruction()
                {
                    Name = planner.Name,
                    Type = "Planner",
                    QueueId = plannerQueue.InstanceId,
                    Enqueued = DateTime.UtcNow.ToJSONString()
                };

                message.CurrentInstruction = plannerInstruction;
                message.Instructions.Add(plannerInstruction);

                await plannerQueue.EnqueueAsync(message);

                return InvokeResult.Success;
            }
            catch (Exception ex)
            {
                PEMBus.InstanceLogger.AddException("ListenerModule_AddStringMessageAsync", ex);
                return InvokeResult.FromException("ListenerModule_AddStringMessageAsync", ex);
            }
        }
    }
}
