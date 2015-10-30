﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace ClusterService
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Domain;
    using Microsoft.ServiceFabric.Data;
    using Microsoft.ServiceFabric.Data.Collections;
    using Microsoft.ServiceFabric.Services;

    public class ClusterService : StatefulService, IClusterService
    {
        internal const string ClusterDictionaryName = "clusterDictionary";
        internal const string SickClusterDictionaryName = "sickClusterDictionary";
        private readonly Random random = new Random();

        private IClusterOperator clusterOperator;
        private IReliableStateManager reliableStateManager;


        public ClusterService()
        {
            this.Config = new ClusterConfig()
            {
                MaximumClusterUptime = TimeSpan.FromMinutes(3),
                MinimumClusterCount = 2,
                MaximumClusterCount = 3,
                RefreshInterval = TimeSpan.FromSeconds(10),
                MaximumUsersPerCluster = 5
            };
            this.clusterOperator = new ArmClusterOperator();
        }

        /// <summary>
        /// Poor-man's dependency injection for now until the API supports proper injection of IReliableStateManager.
        /// This constructor is used in unit tests to inject a different state manager.
        /// </summary>
        /// <param name="stateManager"></param>
        /// <param name="clusterOperator"></param>
        public ClusterService(IClusterOperator clusterOperator, IReliableStateManager stateManager)
            : this()
        {
            this.clusterOperator = clusterOperator;
            this.reliableStateManager = stateManager;
        }

        internal ClusterConfig Config { get; set; }

        public async Task<IEnumerable<ClusterView>> GetClusterListAsync()
        {
            IReliableDictionary<int, Cluster> clusterDictionary =
                await this.reliableStateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(ClusterDictionaryName);

            return from item in clusterDictionary
                   where item.Value.Status == ClusterStatus.Ready
                   orderby item.Value.CreatedOn descending
                   select new ClusterView(
                       item.Key,
                       "Party Cluster " + item.Key,
                       item.Value.AppCount,
                       item.Value.ServiceCount,
                       item.Value.Users.Count,
                    this.Config.MaximumUsersPerCluster,
                    this.Config.MaximumClusterUptime - (DateTimeOffset.UtcNow - item.Value.CreatedOn.ToUniversalTime()));
        }

        public async Task JoinClusterAsync(int clusterId, UserView user)
        {
            if (user == null || String.IsNullOrWhiteSpace(user.UserEmail))
            {
                throw new ArgumentNullException(nameof(user));
            }

            ServiceEventSource.Current.ServiceMessage(this, "Join cluster request. Cluster: {0}.", clusterId);

            IReliableDictionary<int, Cluster> clusterDictionary =
                await this.reliableStateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(ClusterDictionaryName);

            int userPort;
            string clusterAddress;

            using (ITransaction tx = this.reliableStateManager.CreateTransaction())
            {
                ConditionalResult<Cluster> result = await clusterDictionary.TryGetValueAsync(tx, clusterId, LockMode.Update);

                if (!result.HasValue)
                {
                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Join cluster request failed. Cluster does not exist. Cluster ID: {0}.",
                        clusterId);

                    throw new JoinClusterFailedException(JoinClusterFailedReason.ClusterDoesNotExist);
                }

                Cluster cluster = result.Value;

                // make sure the cluster is ready
                if (cluster.Status != ClusterStatus.Ready)
                {
                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Join cluster request failed. Cluster is not ready. Cluster: {0}. Status: {1}",
                        clusterId,
                        cluster.Status);

                    throw new JoinClusterFailedException(JoinClusterFailedReason.ClusterNotReady);
                }

                // make sure the cluster isn't about to be deleted.
                if ((DateTimeOffset.UtcNow - cluster.CreatedOn.ToUniversalTime()) > (this.Config.MaximumClusterUptime))
                {
                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Join cluster request failed. Cluster has expired. Cluster: {0}. Cluster creation time: {1}",
                        clusterId,
                        cluster.CreatedOn.ToUniversalTime());

                    throw new JoinClusterFailedException(JoinClusterFailedReason.ClusterExpired);
                }

                if (cluster.Users.Count >= this.Config.MaximumUsersPerCluster)
                {
                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Join cluster request failed. Cluster is full. Cluster: {0}. Users: {1}",
                        clusterId,
                        cluster.Users.Count);

                    throw new JoinClusterFailedException(JoinClusterFailedReason.ClusterFull);
                }

                if (cluster.Users.Any(x => String.Equals(x.Email, user.UserEmail, StringComparison.OrdinalIgnoreCase)))
                {
                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Join cluster request failed. User already exists. Cluster: {0}.",
                        clusterId);

                    throw new JoinClusterFailedException(JoinClusterFailedReason.UserAlreadyJoined);
                }

                try
                {
                    userPort = cluster.Ports.First(port => !cluster.Users.Any(x => x.Port == port));
                }
                catch (InvalidOperationException)
                {
                    ServiceEventSource.Current.ServiceMessage(
                        this,
                        "Join cluster request failed. No available ports. Cluster: {0}. Users: {1}. Ports: {2}",
                        clusterId,
                        cluster.Users.Count,
                        cluster.Ports.Count());

                    throw new JoinClusterFailedException(JoinClusterFailedReason.NoPortsAvailable);
                }

                clusterAddress = cluster.Address;
                cluster.Users.Add(new ClusterUser(user.UserEmail, userPort));

                await clusterDictionary.SetAsync(tx, clusterId, cluster);

                await tx.CommitAsync();
            }

            ServiceEventSource.Current.ServiceMessage(this, "Join cluster request completed. Cluster: {0}.", clusterId);
            // send email to user with cluster info
        }

        /// <summary>
        /// Poor-man's dependency injection for now until the API supports proper injection of IReliableStateManager.
        /// </summary>
        /// <returns></returns>
        protected override IReliableStateManager CreateReliableStateManager()
        {
            if (this.reliableStateManager == null)
            {
                this.reliableStateManager = base.CreateReliableStateManager();
            }
            return this.reliableStateManager;
        }

        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new[] { new ServiceReplicaListener(parameters => new ServiceCommunicationListener<IClusterService>(parameters, this)) };
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await this.ProcessClustersAsync();

                    int target = await this.GetTargetClusterCapacityAsync();

                    await this.BalanceClustersAsync(target);
                }
                catch (TimeoutException te)
                {
                    ServiceEventSource.Current.ServiceMessage(this, "TimeoutException in RunAsync: {0}.", te.Message);
                }

                await Task.Delay(this.Config.RefreshInterval, cancellationToken);
            }
        }

        private int CreateClusterId()
        {
            return this.random.Next();
        }

        private string CreateClusterInternalName()
        {
            return "party" + (ushort)this.random.Next();
        }

        /// <summary>
        /// Adds clusters by the given amount without going over the max threshold and without resulting in below the min threshold.
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        internal async Task BalanceClustersAsync(int target)
        {

            IReliableDictionary<int, Cluster> clusterDictionary =
                await this.reliableStateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(ClusterDictionaryName);

            using (ITransaction tx = this.reliableStateManager.CreateTransaction())
            {
                IEnumerable<KeyValuePair<int, Cluster>> activeClusters = this.GetActiveClusters(clusterDictionary);
                int activeClusterCount = activeClusters.Count();

                if (target < this.Config.MinimumClusterCount)
                {
                    target = this.Config.MinimumClusterCount;
                }

                if (target > this.Config.MaximumClusterCount)
                {
                    target = this.Config.MaximumClusterCount;
                }

                ServiceEventSource.Current.ServiceMessage(this,
                    "Balancing clusters started. Target: {0} Total active: {1}. New: {2}. Creating: {3}. Ready: {4}.",
                    target,
                    activeClusterCount,
                    activeClusters.Count(x => x.Value.Status == ClusterStatus.New),
                    activeClusters.Count(x => x.Value.Status == ClusterStatus.Creating),
                    activeClusters.Count(x => x.Value.Status == ClusterStatus.Ready));

                if (activeClusterCount < target)
                {
                    int limit = Math.Min(target, this.Config.MaximumClusterCount) - activeClusterCount;

                    for (int i = 0; i < limit; ++i)
                    {
                        await clusterDictionary.AddAsync(tx, this.CreateClusterId(), new Cluster(this.CreateClusterInternalName()));
                    }

                    await tx.CommitAsync();

                    ServiceEventSource.Current.ServiceMessage(this, "Balancing clusters completed. Added: {0}", limit);
                }
                else if (activeClusterCount > target)
                {
                    IEnumerable<KeyValuePair<int, Cluster>> removeList = activeClusters
                        .Where(x => x.Value.Users.Count == 0)
                        .Take(Math.Min(activeClusterCount - this.Config.MinimumClusterCount, activeClusterCount - target));

                    int ix = 0;
                    foreach (KeyValuePair<int, Cluster> item in removeList)
                    {
                        Cluster value = item.Value;
                        value.Status = ClusterStatus.Remove;

                        await clusterDictionary.SetAsync(tx, item.Key, value);
                        ++ix;
                    }

                    await tx.CommitAsync();

                    ServiceEventSource.Current.ServiceMessage(this, "Balancing clusters completed. Marked for removal: {0}", ix);
                }

            }
        }

        /// <summary>
        /// Removes clusters that have been deleted from the list.
        /// </summary>
        /// <returns></returns>
        internal async Task ProcessClustersAsync()
        {
            IReliableDictionary<int, Cluster> clusterDictionary =
                await this.reliableStateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(ClusterDictionaryName);

            IReliableDictionary<int, int> sickClusters =
                await this.reliableStateManager.GetOrAddAsync<IReliableDictionary<int, int>>(SickClusterDictionaryName);

            foreach (KeyValuePair<int, Cluster> cluster in clusterDictionary)
            {
                using (ITransaction tx = this.reliableStateManager.CreateTransaction())
                {
                    try
                    {
                        await this.ProcessClusterStatusAsync(cluster.Value);
                    }
                    catch (Exception e)
                    {
                        ServiceEventSource.Current.ServiceMessage(this, "Failed to process cluster: {0}. {1}", cluster.Value.Address, e.Message);

                        //TODO: process sick clusters with multiple failures.
                        //await sickClusters.AddOrUpdateAsync(tx, cluster.Key, 1, (key, value) => ++value);
                    }

                    if (cluster.Value.Status == ClusterStatus.Deleted)
                    {
                        await clusterDictionary.TryRemoveAsync(tx, cluster.Key);
                    }
                    else
                    {
                        await clusterDictionary.SetAsync(tx, cluster.Key, cluster.Value);
                    }

                    await tx.CommitAsync();
                }
            }
        }

        /// <summary>
        /// Determines how many clusters there should be based on user activity and min/max thresholds.
        /// </summary>
        /// <remarks>
        /// When the user count goes below the low percent threshold, decrease capacity by (high - low)%
        /// When the user count goes above the high percent threshold, increase capacity by (1 - high)%
        /// </remarks>
        /// <returns></returns>
        internal async Task<int> GetTargetClusterCapacityAsync()
        {
            IReliableDictionary<int, Cluster> clusterDictionary =
                await this.reliableStateManager.GetOrAddAsync<IReliableDictionary<int, Cluster>>(ClusterDictionaryName);

            IEnumerable<KeyValuePair<int, Cluster>> activeClusters = this.GetActiveClusters(clusterDictionary);
            int activeClusterCount = activeClusters.Count();

            double totalCapacity = activeClusterCount * this.Config.MaximumUsersPerCluster;

            double totalUsers = activeClusters
                    .Aggregate(0, (total, next) => total += next.Value.Users.Count);

            double percentFull = totalUsers / totalCapacity;

            if (percentFull >= this.Config.UserCapacityHighPercentThreshold)
            {
                return Math.Min(
                    this.Config.MaximumClusterCount,
                    activeClusterCount + (int)Math.Ceiling(activeClusterCount * (1 - this.Config.UserCapacityHighPercentThreshold)));
            }

            if (percentFull <= this.Config.UserCapacityLowPercentThreshold)
            {
                return Math.Max(
                    this.Config.MinimumClusterCount,
                    activeClusterCount -
                    (int)Math.Floor(activeClusterCount * (this.Config.UserCapacityHighPercentThreshold - this.Config.UserCapacityLowPercentThreshold)));
            }

            return activeClusterCount;
        }

        /// <summary>
        /// Processes a cluster based on its current state.
        /// </summary>
        /// <returns></returns>
        internal async Task ProcessClusterStatusAsync(Cluster cluster)
        {
            switch (cluster.Status)
            {
                case ClusterStatus.New:
                    Random random = new Random();
                    try
                    {
                        cluster.Address = await this.clusterOperator.CreateClusterAsync(cluster.InternalName);
                    }
                    catch (InvalidOperationException e)
                    {
                        // cluster with this name might already exist, so remove this one.
                        ServiceEventSource.Current.ServiceMessage(this, "Cluster failed to create: {0}. {1}", cluster.Address, e.Message);
                        cluster.Status = ClusterStatus.Deleted; // mark as deleted so it gets removed from the list.
                    }

                    cluster.Status = ClusterStatus.Creating;

                    ServiceEventSource.Current.ServiceMessage(this, "Creating cluster: {0}", cluster.Address);
                    break;

                case ClusterStatus.Creating:
                    ClusterOperationStatus creatingStatus = await this.clusterOperator.GetClusterStatusAsync(cluster.InternalName);
                    switch (creatingStatus)
                    {
                        case ClusterOperationStatus.Creating:
                            // still creating
                            break;

                        case ClusterOperationStatus.Ready:
                            ServiceEventSource.Current.ServiceMessage(this, "Cluster is ready: {0}", cluster.Address);
                            cluster.Ports = await this.clusterOperator.GetClusterPortsAsync(cluster.InternalName);
                            cluster.CreatedOn = DateTimeOffset.UtcNow;
                            cluster.Status = ClusterStatus.Ready;
                            break;

                        case ClusterOperationStatus.CreateFailed:
                            ServiceEventSource.Current.ServiceMessage(this, "Cluster failed to create: {0}", cluster.Address);
                            cluster.Status = ClusterStatus.Remove;
                            break;

                        case ClusterOperationStatus.Deleting:
                            cluster.Status = ClusterStatus.Deleting;
                            break;
                    }
                    break;

                case ClusterStatus.Ready:
                    if (DateTimeOffset.UtcNow - cluster.CreatedOn.ToUniversalTime() >= this.Config.MaximumClusterUptime)
                    {
                        ServiceEventSource.Current.ServiceMessage(this, "Cluster expired: {0}", cluster.Address);
                        cluster.Status = ClusterStatus.Remove;
                        break;
                    }

                    ClusterOperationStatus readyStatus = await this.clusterOperator.GetClusterStatusAsync(cluster.InternalName);
                    switch (readyStatus)
                    {
                        case ClusterOperationStatus.Deleting:
                            cluster.Status = ClusterStatus.Deleting;
                            break;
                    }

                    //TODO: update application and service count
                    break;

                case ClusterStatus.Remove:
                    ClusterOperationStatus removeStatus = await this.clusterOperator.GetClusterStatusAsync(cluster.InternalName);
                    switch (removeStatus)
                    {
                        case ClusterOperationStatus.Creating:
                        case ClusterOperationStatus.Ready:
                        case ClusterOperationStatus.CreateFailed:
                        case ClusterOperationStatus.DeleteFailed:
                            ServiceEventSource.Current.ServiceMessage(this, "Deleting cluster {0}.", cluster.Address);
                            await this.clusterOperator.DeleteClusterAsync(cluster.InternalName);
                            cluster.Status = ClusterStatus.Deleting;
                            break;

                        case ClusterOperationStatus.Deleting:
                            cluster.Status = ClusterStatus.Deleting;
                            break;

                        case ClusterOperationStatus.ClusterNotFound:
                            cluster.Status = ClusterStatus.Deleted;
                            break;
                    }
                    break;

                case ClusterStatus.Deleting:
                    ClusterOperationStatus deleteStatus = await this.clusterOperator.GetClusterStatusAsync(cluster.InternalName);
                    switch (deleteStatus)
                    {
                        case ClusterOperationStatus.Creating:
                        case ClusterOperationStatus.Ready:
                            cluster.Status = ClusterStatus.Remove; // hopefully shouldn't ever get here
                            break;

                        case ClusterOperationStatus.Deleting:
                            break; // still in progress

                        case ClusterOperationStatus.ClusterNotFound:
                            ServiceEventSource.Current.ServiceMessage(this, "Cluster successfully deleted: {0}.", cluster.Address);
                            cluster.Status = ClusterStatus.Deleted;
                            break;

                        case ClusterOperationStatus.CreateFailed:
                        case ClusterOperationStatus.DeleteFailed:
                            ServiceEventSource.Current.ServiceMessage(this, "Cluster failed to delete: {0}.", cluster.Address);
                            cluster.Status = ClusterStatus.Remove;
                            break;
                    }
                    break;
            }
        }

        private IEnumerable<KeyValuePair<int, Cluster>> GetActiveClusters(IReliableDictionary<int, Cluster> clusterDictionary)
        {
            return clusterDictionary.Where(
                x =>
                x.Value.Status == ClusterStatus.New ||
                x.Value.Status == ClusterStatus.Creating ||
                x.Value.Status == ClusterStatus.Ready);
        }
    }
}