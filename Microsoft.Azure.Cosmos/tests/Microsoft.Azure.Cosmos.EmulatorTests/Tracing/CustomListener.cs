﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Tracing;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Text.RegularExpressions;
    using Microsoft.Azure.Cosmos.Telemetry;
    using Microsoft.Azure.Cosmos.Tracing;

    /// <summary>
    /// It is a custom listener for Activities and Event. It is used to validate the Activities generated by cosmosDb SDK.
    /// </summary>
    internal class CustomListener :
        EventListener, // Override Event Listener to capture Event source events
        IObserver<KeyValuePair<string, object>>, // Override IObserver to capture Activity events
        IObserver<DiagnosticListener>,
        IDisposable
    {
        private readonly Func<string, bool> sourceNameFilter;
        private readonly string eventName;
        private readonly bool suppressAllEvents;
        private static readonly List<string> TagsWithStaticValue = new List<string>
        {
            "az.schema_url",
            "kind",
            "az.namespace",
            "db.operation.name",
            "db.operation",
            "db.system",
            "server.address",
            "db.namespace",
            "db.name",
            "db.collection.name",
            "db.cosmosdb.connection_mode",
            "db.cosmosdb.regions_contacted",
            "db.cosmosdb.consistency_level",
            "db.operation.batch_size",
            "db.query.text",
            "error.type",
            AppInsightClassicAttributeKeys.ContainerName,
            AppInsightClassicAttributeKeys.ServerAddress,
        };

        private static readonly List<string> TagsToSkip = new List<string>
        {
             OpenTelemetryAttributeKeys.ResponseContentLength,
             OpenTelemetryAttributeKeys.RequestContentLength,
             AppInsightClassicAttributeKeys.ResponseContentLength,
             AppInsightClassicAttributeKeys.RequestContentLength
        };

        private ConcurrentBag<IDisposable> subscriptions = new();
        private ConcurrentBag<ProducedDiagnosticScope> Scopes { get; } = new();
        
        public static ConcurrentBag<Activity> CollectedOperationActivities { private set; get; } = new();
        public static ConcurrentBag<Activity> CollectedNetworkActivities { private set; get; } = new();
        private static ConcurrentBag<string> CollectedEvents { set; get; } = new();

        private static List<EventSource> EventSources { set; get; } = new();

        public CustomListener(string name, string eventName, bool suppressAllEvents = false)
            : this(n => Regex.Match(n, name).Success, eventName, suppressAllEvents)
        {
        }

        public CustomListener(Func<string, bool> filter, string eventName, bool suppressAllEvents = false)
        {
            this.sourceNameFilter = filter;
            this.eventName = eventName;
            this.suppressAllEvents = suppressAllEvents;

            foreach (EventSource eventSource in EventSources)
            {
                this.OnEventSourceCreated(eventSource);
            }

            DiagnosticListener.AllListeners.Subscribe(this);
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnCompleted()
        {
            // Unimplemented Method
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnError(Exception error)
        {
            // Unimplemented Method
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnNext(KeyValuePair<string, object> value)
        {
            lock (this.Scopes)
            {
                // Check for disposal
                if (this.subscriptions == null) return;

                string startSuffix = ".Start";
                string stopSuffix = ".Stop";
                string exceptionSuffix = ".Exception";

                if (value.Key.EndsWith(startSuffix))
                {
                    string name = value.Key[..^startSuffix.Length];
                    PropertyInfo propertyInfo = value.Value.GetType().GetTypeInfo().GetDeclaredProperty("Links");
                    IEnumerable<Activity> links = propertyInfo?.GetValue(value.Value) as IEnumerable<Activity> ?? Array.Empty<Activity>();

                    ProducedDiagnosticScope scope = new ProducedDiagnosticScope()
                    {
                        Name = name,
                        Activity = Activity.Current,
                        Links = links.Select(a => new ProducedLink(a.ParentId, a.TraceStateString)).ToList(),
                        LinkedActivities = links.ToList()
                    };
                    this.Scopes.Add(scope);
                }
                else if (value.Key.EndsWith(stopSuffix))
                {
                    string name = value.Key[..^stopSuffix.Length];
                    foreach (ProducedDiagnosticScope producedDiagnosticScope in this.Scopes)
                    {
                        if (producedDiagnosticScope.Activity.Id == Activity.Current.Id)
                        {
                            if (producedDiagnosticScope.Activity.OperationName.StartsWith("Operation."))
                            {
                                AssertActivity.IsValidOperationActivity(producedDiagnosticScope.Activity);
                                CustomListener.CollectedOperationActivities.Add(producedDiagnosticScope.Activity);
                            }
                            else if (producedDiagnosticScope.Activity.OperationName.StartsWith("Request."))
                            {
                                CustomListener.CollectedNetworkActivities.Add(producedDiagnosticScope.Activity);
                            }
                            
                            producedDiagnosticScope.IsCompleted = true;
                            return;
                        }
                    }
                    throw new InvalidOperationException($"Event '{name}' was not started");
                }
                else if (value.Key.EndsWith(exceptionSuffix))
                {
                    string name = value.Key[..^exceptionSuffix.Length];
                    foreach (ProducedDiagnosticScope producedDiagnosticScope in this.Scopes)
                    {
                        if (producedDiagnosticScope.Activity.Id == Activity.Current.Id)
                        {
                            if (producedDiagnosticScope.IsCompleted)
                            {
                                throw new InvalidOperationException("Scope should not be stopped when calling Failed");
                            }
                            producedDiagnosticScope.Exception = (Exception)value.Value;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// IObserver Override
        /// </summary>
        public void OnNext(DiagnosticListener value)
        {
            if (this.sourceNameFilter(value.Name) && this.subscriptions != null)
            {
                lock (this.Scopes)
                {
                    IDisposable subscriber = value.Subscribe(this, isEnabled: (name) =>
                    {
                        if (this.suppressAllEvents)
                        {
                            return false;
                        }
                        return true;
                    });
                    this.subscriptions?.Add(subscriber);
                }
            }
        }

        /// <summary>
        /// EventListener Override
        /// </summary>
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (this.eventName == null)
            {
                EventSources.Add(eventSource);
            }

            if (this.eventName == null)
            {
                EventSources.Add(eventSource);
            }

            if (eventSource != null && eventSource.Name.Equals(this.eventName))
            {
                this.EnableEvents(eventSource, EventLevel.Informational); // Enable information level events
            }
        }

        /// <summary>
        /// EventListener Override
        /// </summary>
        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append($"<EVENT name='{eventData.EventName}'/>");
            
            CustomListener.CollectedEvents.Add(builder.ToString());
        }
        
        /// <summary>
        /// Dispose Override
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        public override void Dispose()
        {
            base.Dispose();

            if (this.subscriptions == null)
            {
                return;
            }

            ConcurrentBag<IDisposable> subscriptions;
            lock (this.Scopes)
            {
                subscriptions = this.subscriptions;
                this.subscriptions = null;
            }

            foreach (IDisposable subscription in subscriptions)
            {
                subscription.Dispose();
            }

            foreach (ProducedDiagnosticScope producedDiagnosticScope in this.Scopes)
            {
                Activity activity = producedDiagnosticScope.Activity;
                string operationName = activity.OperationName;
                // traverse the activities and check for duplicates among ancestors
                while (activity != null)
                {
                    if (operationName == activity.Parent?.OperationName)
                    {
                        // Throw this exception lazily on Dispose, rather than when the scope is started, so that we don't trigger a bunch of other
                        // erroneous exceptions relating to scopes not being completed/started that hide the actual issue
                        throw new InvalidOperationException($"A scope has already started for event '{producedDiagnosticScope.Name}'");
                    }

                    activity = activity.Parent;
                }

                if (!producedDiagnosticScope.IsCompleted)
                {
                    throw new InvalidOperationException($"'{producedDiagnosticScope.Name}' scope is not completed");
                }
            }

            this.ResetAttributes();
        }
        
        private string GenerateTagForBaselineTest(Activity activity)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append($"<ACTIVITY source='{activity.Source.Name}' operationName='{activity.OperationName}' displayName='{activity.DisplayName}'>");

            foreach (KeyValuePair<string, object> tag in activity.TagObjects)
            {
                if (TagsToSkip.Contains(tag.Key))
                {
                    continue;
                }

                if (TagsWithStaticValue.Contains(tag.Key))
                {
                    builder
                    .Append($"<ATTRIBUTE key='{tag.Key}'>{tag.Value}</ATTRIBUTE>");
                }
                else
                {
                    builder
                    .Append($"<ATTRIBUTE key='{tag.Key}'>Some Value</ATTRIBUTE>");
                }
            }
            
            builder.Append("</ACTIVITY>");
            
            return builder.ToString();
        }
        
        public List<string> GetRecordedAttributes() 
        {
            List<string> generatedActivityTagsForBaselineXmls = new();
            
            // Get all the recorded operation level activities
            List<Activity> collectedOperationActivities = new List<Activity>(CustomListener.CollectedOperationActivities);

            // Order them by the static values. This is to make sure that the order of the attributes is always same.
            List<Activity> orderedUniqueOperationActivities = collectedOperationActivities
               .OrderBy(act =>
               {
                   string key = act.Source.Name + act.OperationName;
                   foreach (string tagName in TagsWithStaticValue)
                   {
                       key += act.GetTagItem(tagName);
                   }
                   return key;
               }).ToList();

            // Generate XML tags for Baseline xmls
            foreach (Activity activity in collectedOperationActivities)
            {
                generatedActivityTagsForBaselineXmls.Add(this.GenerateTagForBaselineTest(activity));
            }

            // Get all the recorded network level activities
            HashSet<Activity> collectedNetworkActivities = new HashSet<Activity>(CustomListener.CollectedNetworkActivities, new NetworkActivityComparer());
            
            // Order them by the static values. This is to make sure that the order of the attributes is always same.
            List<Activity> orderedUniqueNetworkActivities = collectedNetworkActivities
                .OrderBy(act => 
                            act.Source.Name + 
                            act.OperationName + 
                            act.GetTagItem("rntbd.status_code") + 
                            act.GetTagItem("rntbd.sub_status_code"))
                .ToList();

            // Generate XML tags for Baseline xmls
            foreach (Activity activity in orderedUniqueNetworkActivities)
            {
                generatedActivityTagsForBaselineXmls.Add(this.GenerateTagForBaselineTest(activity));
            }

            List<string> outputList = new List<string>();
            if(generatedActivityTagsForBaselineXmls != null && generatedActivityTagsForBaselineXmls.Count > 0)
            {
                outputList.AddRange(generatedActivityTagsForBaselineXmls);

            }
            if (CustomListener.CollectedEvents != null && CustomListener.CollectedEvents.Count > 0)
            {
                outputList.AddRange(CustomListener.CollectedEvents);
            }

            return outputList;
        }

        public void ResetAttributes()
        {
            CustomListener.CollectedEvents = new();
            CustomListener.CollectedOperationActivities = new();
            CustomListener.CollectedNetworkActivities = new();
        }

        public class ProducedDiagnosticScope
        {
            public string Name { get; set; }
            public Activity Activity { get; set; }
            public bool IsCompleted { get; set; }
            public bool IsFailed => this.Exception != null;
            public Exception Exception { get; set; }
            public List<ProducedLink> Links { get; set; } = new List<ProducedLink>();
            public List<Activity> LinkedActivities { get; set; } = new List<Activity>();

            public override string ToString()
            {
                return this.Name;
            }
        }

        public struct ProducedLink
        {
            public ProducedLink(string id)
            {
                this.Traceparent = id;
                this.Tracestate = null;
            }

            public ProducedLink(string traceparent, string tracestate)
            {
                this.Traceparent = traceparent;
                this.Tracestate = tracestate;
            }

            public string Traceparent { get; set; }
            public string Tracestate { get; set; }
        }

        public class NetworkActivityComparer : IEqualityComparer<Activity>
        {
            public bool Equals(Activity x, Activity y)
            {
                string xData = x.Source.Name + x.OperationName + x.GetTagItem("rntbd.status_code") + x.GetTagItem("rntbd.sub_status_code");
                string yData = y.Source.Name + y.OperationName + y.GetTagItem("rntbd.status_code") + y.GetTagItem("rntbd.sub_status_code");

                return xData.Equals(yData, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(Activity obj)
            {
                return (obj.Source.Name + obj.OperationName + obj.GetTagItem("rntbd.status_code") + obj.GetTagItem("rntbd.sub_status_code")).GetHashCode() ;
            }
        }

    }
}
