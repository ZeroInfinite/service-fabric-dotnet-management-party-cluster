﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace PartyCluster.ClusterService
{
    using System;

    internal class ClusterConfig
    {
        public ClusterConfig()
        {
            this.RefreshInterval = TimeSpan.FromSeconds(1);
            this.MinimumClusterCount = 10;
            this.MaximumClusterCount = 100;
            this.MaximumUsersPerCluster = 1;
            this.MaximumClusterUptime = TimeSpan.FromHours(1);
            this.UserCapacityHighPercentThreshold = 0.75;
            this.UserCapacityLowPercentThreshold = 0.25;
        }

        public TimeSpan RefreshInterval { get; set; }

        public int MinimumClusterCount { get; set; }

        public int MaximumClusterCount { get; set; }

        public int MaximumUsersPerCluster { get; set; }

        public TimeSpan MaximumClusterUptime { get; set; }

        public double UserCapacityHighPercentThreshold { get; set; }

        public double UserCapacityLowPercentThreshold { get; set; }

        public double CapacityThresholdIncrement { get; set; }
    }
}