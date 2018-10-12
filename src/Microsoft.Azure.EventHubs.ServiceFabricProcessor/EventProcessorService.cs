﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.using System;

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Description;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Runtime;


namespace Microsoft.Azure.EventHubs.ServiceFabricProcessor
{
    /// <summary>
    /// Base class that implements event processor functionality.
    /// </summary>
    /// <typeparam name="TEventProcessor">The type of the user's implementation of IEventProcessor</typeparam>
    public class EventProcessorService<TEventProcessor> : IPartitionReceiveHandler
        where TEventProcessor : IEventProcessor, new()
    {
        private PartitionContext partitionContext = null;
        private EventHubsConnectionStringBuilder ehConnectionString;
        private string consumerGroupName;
        private int partitionOrdinal = -1;
        private string partitionId = null;
        private int servicePartitions = -1;
        private string initialOffset = null;
        private CancellationTokenSource internalCanceller;
        private Exception internalFatalException = null;
        private IEventProcessor userEventProcessor = null;
        private CancellationToken linkedCancellationToken;

        private IReliableStateManager ServiceStateManager;
        private StatefulServiceContext ServiceContext;
        private IStatefulServicePartition ServicePartition;

        /// <summary>
        /// Constructor required by Service Fabric.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="stateManager"></param>
        /// <param name="partition"></param>
        public EventProcessorService(StatefulServiceContext context, IReliableStateManager stateManager, IStatefulServicePartition partition)
        {
            this.ServiceContext = context;
            this.ServiceStateManager = stateManager;
            this.ServicePartition = partition;

            this.Options = new EventProcessorOptions();
            this.EventProcessorFactory = new DefaultEventProcessorFactory<TEventProcessor>();
            this.CheckpointManager = new ReliableDictionaryCheckpointMananger(this.ServiceStateManager);
            this.EventHubClientFactory = new EventHubWrappers.EventHubClientFactory();
            this.TestMode = false;

            this.internalCanceller = new CancellationTokenSource();
        }

        /// <summary>
        /// Set processing options in the constructor.
        /// </summary>
        public EventProcessorOptions Options { get; set; }

        /// <summary>
        /// Optionally provide a user implementation of the event processor factory.
        /// </summary>
        public IEventProcessorFactory EventProcessorFactory { get; set; }

        /// <summary>
        /// Optionally provide a user implementation of the checkpoint manager.
        /// </summary>
        public ICheckpointMananger CheckpointManager { get; set; }

        /// <summary>
        /// For testing purposes.
        /// </summary>
        public EventHubWrappers.IEventHubClientFactory EventHubClientFactory { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public bool TestMode { get; set; }

        /// <summary>
        /// Called by Service Fabric.
        /// </summary>
        /// <param name="fabricCancellationToken"></param>
        /// <returns></returns>
        public async Task RunAsync(CancellationToken fabricCancellationToken)
        {
            try
            {
                using (CancellationTokenSource linkedCanceller = CancellationTokenSource.CreateLinkedTokenSource(fabricCancellationToken, this.internalCanceller.Token))
                {
                    this.linkedCancellationToken = linkedCanceller.Token;
                    await InnerRunAsync();
                    this.Options.NotifyOnShutdown(null);
                }
            }
            catch (Exception e)
            {
                // If InnerRunAsync throws, that is intended to be a fatal exception for this instance.
                // Catch it here just long enough to log and notify, then rethrow.

                EventProcessorEventSource.Current.Message("THROWING OUT: {0}", e);
                this.Options.NotifyOnShutdown(e);
                throw e;
            }
        }

        private async Task InnerRunAsync()
        {
            EventHubWrappers.IEventHubClient ehclient = null;
            EventHubWrappers.IPartitionReceiver receiver = null;

            try
            {
                //
                // General startup tasks.
                //
                await PartitionStartup(this.linkedCancellationToken);

                //
                // Create EventHubClient and check partition count.
                //
                Exception lastException = null;
                EventProcessorEventSource.Current.Message("Creating event hub client");
                for (int i = 0; i < Constants.RetryCount; i++)
                {
                    this.linkedCancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        ehclient = this.EventHubClientFactory.CreateFromConnectionString(this.ehConnectionString.ToString());
                        break;
                    }
                    catch (EventHubsException e)
                    {
                        if (!e.IsTransient)
                        {
                            // Nontransient exceptions when creating the client are fatal and throw out of RunAsync.
                            throw e;
                        }
                        lastException = e;
                    }
                }
                if (ehclient == null)
                {
                    EventProcessorEventSource.Current.Message("Out of retries event hub client");
                    throw new Exception("Out of retries creating EventHubClient", lastException);
                }
                EventProcessorEventSource.Current.Message("Event hub client OK");
                EventProcessorEventSource.Current.Message("Getting event hub info");
                EventHubRuntimeInformation ehInfo = null;
                for (int i = 0; i < Constants.RetryCount; i++)
                {
                    this.linkedCancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        ehInfo = await ehclient.GetRuntimeInformationAsync();
                        break;
                    }
                    catch (EventHubsException e)
                    {
                        if (!e.IsTransient)
                        {
                            // Nontransient exceptions here are fatal and throw out of RunAsync.
                            throw e;
                        }
                        lastException = e;
                    }
                }
                if (ehInfo == null)
                {
                    EventProcessorEventSource.Current.Message("Out of retries getting event hub info");
                    throw new Exception("Out of retries getting event hub runtime info", lastException);
                }
                if (this.TestMode)
                {
                    if (this.servicePartitions > ehInfo.PartitionCount)
                    {
                        EventProcessorEventSource.Current.Message("TestMode requires event hub partition count larger than service partitinon count");
                        throw new EventProcessorConfigurationException("TestMode requires event hub partition count larger than service partitinon count");
                    }
                    else if (this.servicePartitions < ehInfo.PartitionCount)
                    {
                        EventProcessorEventSource.Current.Message("TestMode: receiving from subset of event hub");
                    }
                }
                else if (ehInfo.PartitionCount != this.servicePartitions)
                {
                    EventProcessorEventSource.Current.Message("Service partition count {0} does not match event hub partition count {1}", this.servicePartitions, ehInfo.PartitionCount);
                    throw new EventProcessorConfigurationException("Service partition count " + this.servicePartitions + " does not match event hub partition count " + ehInfo.PartitionCount);
                }
                this.partitionId = ehInfo.PartitionIds[this.partitionOrdinal];

                //
                // Generate a PartitionContext now that the required info is available.
                //
                this.partitionContext = new PartitionContext(this.linkedCancellationToken, this.partitionId, this.ehConnectionString.EntityPath, this.consumerGroupName, this.CheckpointManager);

                //
                // Start up checkpoint manager.
                //
                await CheckpointStartup(this.linkedCancellationToken);

                //
                // If there was a checkpoint, the offset is in this.initialOffset, so convert it to an EventPosition.
                // If no checkpoint, get starting point from user-supplied provider.
                //
                EventPosition initialPosition = null;
                if (this.initialOffset != null)
                {
                    EventProcessorEventSource.Current.Message("Initial position from checkpoint, offset {0}", this.initialOffset);
                    initialPosition = EventPosition.FromOffset(this.initialOffset);
                }
                else
                {
                    initialPosition = this.Options.InitialPositionProvider(this.partitionId);
                    EventProcessorEventSource.Current.Message("Initial position from provider");
                }

                //
                // Create receiver.
                //
                EventProcessorEventSource.Current.Message("Creating receiver");
                for (int i = 0; i < Constants.RetryCount; i++)
                {
                    this.linkedCancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        receiver = ehclient.CreateEpochReceiver(this.consumerGroupName, this.partitionId, initialPosition, this.initialOffset, Constants.FixedReceiverEpoch, null); // FOO receiveroptions
                        break;
                    }
                    catch (EventHubsException e)
                    {
                        if (!e.IsTransient)
                        {
                            throw e;
                        }
                        lastException = e;
                    }
                }
                if (receiver == null)
                {
                    EventProcessorEventSource.Current.Message("Out of retries creating receiver");
                    throw new Exception("Out of retries creating event hub receiver", lastException);
                }

                //
                // Instantiate user's event processor class and call Open.
                // If user's Open code fails, treat that as a fatal exception and let it throw out.
                //
                EventProcessorEventSource.Current.Message("Creating event processor");
                this.userEventProcessor = this.EventProcessorFactory.CreateEventProcessor(this.linkedCancellationToken, this.partitionContext);
                await this.userEventProcessor.OpenAsync(this.linkedCancellationToken, this.partitionContext);
                EventProcessorEventSource.Current.Message("Event processor created and opened OK");

                //
                // Start metrics reporting. This runs as a separate background thread.
                //
                Thread t = new Thread(this.MetricsHandler);
                t.Start();

                //
                // Receive pump.
                //
                EventProcessorEventSource.Current.Message("RunAsync setting handler and waiting");
                this.MaxBatchSize = this.Options.MaxBatchSize;
                receiver.SetReceiveHandler(this, Options.InvokeProcessorAfterReceiveTimeout);
                this.linkedCancellationToken.WaitHandle.WaitOne();

                EventProcessorEventSource.Current.Message("RunAsync continuing, cleanup");
            }
            finally
            {
                if (this.userEventProcessor != null)
                {
                    // partitionContext is set up before processor is created, so it is available if processor is not null
                    await this.userEventProcessor.CloseAsync(this.partitionContext, this.linkedCancellationToken.IsCancellationRequested ? CloseReason.Cancelled : CloseReason.Failure);
                }
                if (receiver != null)
                {
                    receiver.SetReceiveHandler(null);
                    await receiver.CloseAsync();
                }
                if (ehclient != null)
                {
                    await ehclient.CloseAsync();
                }
                if (this.internalFatalException != null)
                {
                    throw this.internalFatalException;
                }
            }
        }

        /// <summary>
        /// From IPartitionReceiveHandler
        /// </summary>
        public int MaxBatchSize { get; set; }

        async Task IPartitionReceiveHandler.ProcessEventsAsync(IEnumerable<EventData> events)
        {
            if ((events != null) || ((events == null) && Options.InvokeProcessorAfterReceiveTimeout))
            {
                IEnumerable<EventData> effectiveEvents = events;

                if (effectiveEvents != null)
                {
                    // Save position of last event
                    IEnumerator<EventData> scanner = effectiveEvents.GetEnumerator();
                    EventData last = null;
                    while (scanner.MoveNext())
                    {
                        last = scanner.Current;
                    }
                    if (last != null)
                    {
                        this.partitionContext.SetOffsetAndSequenceNumber(last);
                        if (this.Options.EnableReceiverRuntimeMetric)
                        {
                            this.partitionContext.RuntimeInformation.Update(last);
                        }
                    }
                }
                else
                {
                    // Client returns null on timeout, but processor expects empty enumerable.
                    effectiveEvents = new List<EventData>();
                }

                IEventProcessor capturedEventProcessor = this.userEventProcessor;
                if (capturedEventProcessor != null)
                {
                    await capturedEventProcessor.ProcessEventsAsync(this.linkedCancellationToken, this.partitionContext, effectiveEvents);
                }

                foreach (EventData ev in effectiveEvents)
                {
                    ev.Dispose();
                }
            }
        }

        Task IPartitionReceiveHandler.ProcessErrorAsync(Exception error)
        {
            EventProcessorEventSource.Current.Message("RECEIVE EXCEPTION on {0}: {1}", this.partitionId, error);
            this.userEventProcessor.ProcessErrorAsync(this.partitionContext, error);
            if (error is EventHubsException)
            {
                if (!(error as EventHubsException).IsTransient)
                {
                    this.internalFatalException = error;
                    this.internalCanceller.Cancel();
                }
                // else don't cancel on transient errors
            }
            else
            {
                // All other exceptions are assumed fatal.
                this.internalCanceller.Cancel();
            }
            return Task.CompletedTask;
        }

        private async Task PartitionStartup(CancellationToken cancellationToken)
        {
            // What partition is this? What is the total count of partitions?
            await GetServicePartitionId(cancellationToken);

            // Get event hub connection string from configuration. This is mandatory, cannot proceed without.
            string rawConnectionString = GetConfigurationValue(Constants.EventHubConnectionStringConfigName, null);
            if (rawConnectionString == null)
            {
                throw new EventProcessorConfigurationException("Event hub connection string not supplied in configuration section " + Constants.EventProcessorConfigSectionName);
            }
            EventProcessorEventSource.Current.Message("Event hub connection string {0}", rawConnectionString);
            this.ehConnectionString = new EventHubsConnectionStringBuilder(rawConnectionString);

            // Get consumer group name. Many users will be using the default consumer group, so for convenience default to that if not supplied.
            this.consumerGroupName = GetConfigurationValue(Constants.EventHubConsumerGroupConfigName, Constants.EventHubConsumerGroupConfigDefault);
            EventProcessorEventSource.Current.Message("Consumer group {0}", this.consumerGroupName);
        }

        private async Task CheckpointStartup(CancellationToken cancellationToken)
        {
            // Set up store and get checkpoint, if any.
            await this.CheckpointManager.CreateCheckpointStoreIfNotExistsAsync(cancellationToken);
            Checkpoint checkpoint = await this.CheckpointManager.CreateCheckpointIfNotExistsAsync(this.partitionId, cancellationToken);
            if (!checkpoint.Valid)
            {
                // Not actually any existing checkpoint.
                this.initialOffset = null;
                EventProcessorEventSource.Current.Message("No checkpoint");
            }
            else if (checkpoint.Version == 1)
            {
                this.initialOffset = checkpoint.Offset;
                EventProcessorEventSource.Current.Message("Checkpoint says to start at {0}", this.initialOffset);
            }
            else
            {
                // It's actually a later-version checkpoint but we don't know the details.
                // Access it via the V1 interface and hope it does something sensible.
                this.initialOffset = checkpoint.Offset;
                EventProcessorEventSource.Current.Message("Checkpoint version error");
            }
        }

        private async Task GetServicePartitionId(CancellationToken cancellationToken)
        {
            if (this.partitionOrdinal == -1)
            {
                using (var fabricClient = new FabricClient())
                {
                    var partitionList =
                        await fabricClient.QueryManager.GetPartitionListAsync(this.ServiceContext.ServiceName);

                    //Set the number of partitions
                    this.servicePartitions = partitionList.Count;

                    //Which partition is this one?
                    for (var a = 0; a < partitionList.Count; a++)
                    {
                        if (partitionList[a].PartitionInformation.Id == this.ServiceContext.PartitionId)
                        {
                            this.partitionOrdinal = a;
                            break;
                        }
                    }

                    EventProcessorEventSource.Current.Message($"Total partitions {this.servicePartitions}");
                }
            }
        }

        private string GetConfigurationValue(string configurationValueName, string defaultValue = null)
        {
            string value = defaultValue;
            ConfigurationPackage configurationPackage = this.ServiceContext.CodePackageActivationContext.GetConfigurationPackageObject(Constants.ConfigurationPackageName);
            try
            {
                ConfigurationSection configurationSection = configurationPackage.Settings.Sections[Constants.EventProcessorConfigSectionName];
                ConfigurationProperty configurationProperty = configurationSection.Parameters[configurationValueName];
                value = configurationProperty.Value;
            }
            catch (KeyNotFoundException)
            {
                // If the user has not specified a value in config, drop through and return the default value.
                // If the caller cannot continue without a user-supplied value, it is up to the caller to detect and handle.
            }
            //catch (ArgumentNullException) if configurationValueName is null, that's a code bug, do not catch
            return value;
        }

        private void MetricsHandler()
        {
            EventProcessorEventSource.Current.Message("METRIC reporter starting");

            IEventProcessor capturedProcessor = this.userEventProcessor;
            while (!this.linkedCancellationToken.IsCancellationRequested)
            {
                Dictionary<string, int> userMetrics = capturedProcessor.GetLoadMetric(this.linkedCancellationToken, this.partitionContext);

                try
                {
                    List<LoadMetric> reportableMetrics = new List<LoadMetric>();
                    foreach (KeyValuePair<string, int> metric in userMetrics)
                    {
                        EventProcessorEventSource.Current.Message("METRIC {0} for partition {1} is {2}", metric.Key, this.partitionContext.PartitionId, metric.Value);
                        reportableMetrics.Add(new LoadMetric(metric.Key, metric.Value));
                    }
                    this.ServicePartition.ReportLoad(reportableMetrics);
                    Task.Delay(Constants.MetricReportingInterval, this.linkedCancellationToken).Wait(); // throws on cancel
                }
                catch (Exception e)
                {
                    EventProcessorEventSource.Current.Message("METRIC partition {0} exception {1}", this.partitionContext.PartitionId, e);
                }
            }

            EventProcessorEventSource.Current.Message("METRIC reporter exiting");
        }
    }
}