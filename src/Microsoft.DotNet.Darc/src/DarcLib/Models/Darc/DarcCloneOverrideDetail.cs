// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace Microsoft.DotNet.DarcLib.Models.Darc
{
    public class DarcCloneOverrideDetail
    {
        public static IEnumerable<DarcCloneOverrideDetail> ParseAll(XmlNode xml)
        {
            return
                (xml ?? throw new ArgumentNullException(nameof(xml)))
                .SelectNodes("DarcCloneOverride")
                ?.OfType<XmlNode>()
                .Select(n => new DarcCloneOverrideDetail
                {
                    Repo = n.Attributes[nameof(Repo)].Value?.Trim(),
                    FindDependencies = DarcCloneOverrideFindDependency.ParseAll(n)
                })
                ?? Enumerable.Empty<DarcCloneOverrideDetail>();
        }

        public string Repo { get; set; }

        public IEnumerable<DarcCloneOverrideFindDependency> FindDependencies { get; set; }

    }
}
