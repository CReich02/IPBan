﻿/*
MIT License

Copyright (c) 2012-present Digital Ruby, LLC - https://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DigitalRuby.IPBanCore
{
    /// <summary>
    /// Parse and create a filter for ips, user names, ip list from urls, regex, etc.
    /// </summary>
    public class IPBanFilter : IIPBanFilter
    {
        /// <summary>
        /// Delimiter for each item in a filter string
        /// </summary>
        public const char ItemDelimiter = ',';

        /// <summary>
        /// Delimiter for each item in a filter string
        /// </summary>
        public const string ItemDelimiterString = ",";

        /// <summary>
        /// Item pieces delimiter |
        /// </summary>
        public const char ItemPiecesDelimiter = '|';

        /// <summary>
        /// Item pieces delimiter |
        /// </summary>
        public const string ItemPiecesDelimiterString = "|";

        /// <summary>
        /// Used if multiple ips in one entry (rare)
        /// </summary>
        public const char SubEntryDelimiter = ';';

        /// <summary>
        /// Used if multiple ips in one entry (rare)
        /// </summary>
        public const string SubEntryDelimiterString = ";";

        /// <summary>
        /// Legacy item pieces delimiter ?
        /// </summary>
        private const char itemPiecesDelimiterLegacy = '?';

        private static readonly HashSet<string> ignoreListEntries =
        [
            "0.0.0.0",
            "::0",
            "127.0.0.1",
            "::1",
            "localhost"
        ];

        private static readonly IEnumerable<KeyValuePair<string, object>> ipListHeaders =
        [
            new("User-Agent", "ipban.com")
        ];

        private readonly HashSet<System.Net.IPAddress> set = [];
        private readonly string value;
        private readonly Regex regex;
        private readonly HashSet<IPAddressRange> ranges = [];
        private readonly HashSet<string> others = new(StringComparer.OrdinalIgnoreCase);
        private readonly IDnsServerList dnsList;
        private readonly IIPBanFilter counterFilter;

        private void AddIPAddressRange(IPAddressRange range)
        {
            if (range.Single)
            {
                lock (set)
                {
                    set.Add(range.Begin);
                }
            }
            else
            {
                lock (ranges)
                {
                    ranges.Add(range);
                }
            }
        }

        private bool IsNonIPMatch(string entry, out string reason)
        {
            if (!string.IsNullOrWhiteSpace(entry))
            {
                entry = entry.Trim().Normalize();

                if (others.Contains(entry))
                {
                    // direct string match in other set
                    reason = "Other";
                    return true;
                }

                // fallback to regex match
                if (regex is not null)
                {
                    // try the regex as last resort
                    if (regex.IsMatch(entry))
                    {
                        reason = "Regex";
                        return true;
                    }
                }
            }

            reason = string.Empty;
            return false;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="value">String value to parse</param>
        /// <param name="regexValue">Regex value to parse</param>
        /// <param name="httpRequestMaker">Http request maker in case urls are present in the value</param>
        /// <param name="dns">Dns lookup in case dns entries are present in the value</param>
        /// <param name="dnsList">Dns servers, these are never filtered</param>
        /// <param name="counterFilter">Filter to check first and return false if contains</param>
        public IPBanFilter(string value, string regexValue, IHttpRequestMaker httpRequestMaker, IDnsLookup dns,
            IDnsServerList dnsList, IIPBanFilter counterFilter)
        {
            this.dnsList = dnsList;
            this.counterFilter = counterFilter;

            value = (value ?? string.Empty).Trim();
            regexValue ??= string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
            {
                List<string> entries = [];

                // primary entries (entry?timestamp?notes) are delimited by comma
                // | can be used as a sub delimiter instead of ? mark
                foreach (string entry in value.Split(ItemDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string entryWithoutComment = GetEntryWithoutComment(entry);

                    // sub entries (multiple ip addresses) are delimited by semi-colon
                    foreach (string subEntry in entryWithoutComment.Split(SubEntryDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        entries.Add(subEntry);
                    }
                }
                List<Task> entryTasks = [];

                // iterate in parallel for performance
                foreach (string entry in entries)
                {
                    string entryWithoutComment = entry;
                    entryTasks.Add(Task.Run(async () =>
                    {
                        bool isUserName;
                        if (entryWithoutComment.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                        {
                            isUserName = true;
                            entryWithoutComment = entryWithoutComment["user:".Length..];
                        }
                        else
                        {
                            isUserName = false;
                        }
                        if (!ignoreListEntries.Contains(entryWithoutComment))
                        {
                            if (!isUserName && IPAddressRange.TryParse(entryWithoutComment, out IPAddressRange rangeFromEntry))
                            {
                                AddIPAddressRange(rangeFromEntry);
                            }
                            else if (!isUserName &&
                                (entryWithoutComment.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                entryWithoutComment.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                            {
                                try
                                {
                                    if (httpRequestMaker is not null)
                                    {
                                        // assume url list of ips, newline delimited
                                        byte[] ipListBytes = null;
                                        Uri uri = new(entryWithoutComment);
                                        await ExtensionMethods.RetryAsync(async () => ipListBytes = await httpRequestMaker.MakeRequestAsync(uri, null, ipListHeaders));
                                        string ipList = Encoding.UTF8.GetString(ipListBytes);
                                        if (!string.IsNullOrWhiteSpace(ipList))
                                        {
                                            foreach (string item in ipList.Split('\n'))
                                            {
                                                if (IPAddressRange.TryParse(item.Trim(), out IPAddressRange ipRangeFromUrl))
                                                {
                                                    AddIPAddressRange(ipRangeFromUrl);
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Error(ex, "Failed to get ip list from url {0}", entryWithoutComment);
                                }
                            }
                            else if (!isUserName && Uri.CheckHostName(entryWithoutComment) != UriHostNameType.Unknown)
                            {
                                try
                                {
                                    if (dns is not null)
                                    {
                                        // add entries for each ip address that matches the dns entry
                                        IPAddress[] addresses = null;
                                        await ExtensionMethods.RetryAsync(async () => addresses = await dns.GetHostAddressesAsync(entryWithoutComment),
                                            exceptionRetry: _ex =>
                                            {
                                                // ignore host not found errors
                                                return (_ex is not System.Net.Sockets.SocketException socketEx ||
                                                        socketEx.SocketErrorCode != System.Net.Sockets.SocketError.HostNotFound);
                                            });

                                        lock (set)
                                        {
                                            foreach (IPAddress adr in addresses)
                                            {
                                                set.Add(adr);
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger.Debug("Unable to resolve dns for {0}: {1}", entryWithoutComment, ex.Message);

                                    lock (others)
                                    {
                                        // eat exception, nothing we can do
                                        others.Add(entryWithoutComment);
                                    }
                                }
                            }
                            else
                            {
                                lock (others)
                                {
                                    others.Add(entryWithoutComment);
                                }
                            }
                        }
                    }));
                }

                Task.WhenAll(entryTasks).Sync();
            }

            this.value = value;

            if (!string.IsNullOrWhiteSpace(regexValue))
            {
                regex = IPBanRegexParser.ParseRegex(regexValue);
            }
        }

        /// <inheritdoc />
        public bool Equals(IIPBanFilter other)
        {
            if (object.ReferenceEquals(this, other))
            {
                return true;
            }
            else if (other is not IPBanFilter otherFilter)
            {
                return false;
            }
            else if (!set.SetEquals(otherFilter.set))
            {
                return false;
            }
            else if (!ranges.SetEquals(otherFilter.ranges))
            {
                return false;
            }
            else if (regex is not null ^ otherFilter.regex is not null)
            {
                return false;
            }
            else if (regex is not null && otherFilter.regex is not null &&
                !regex.ToString().Equals(otherFilter.Regex.ToString()))
            {
                return false;
            }
            return true;
        }

        /// <inheritdoc />
        public bool IsFiltered(string entry, out string reason)
        {
            if (IPAddressRange.TryParse(entry, out IPAddressRange ipAddressRange) &&
                IsFiltered(ipAddressRange, out reason))
            {
                return true;
            }
            else if (counterFilter is not null && counterFilter.IsFiltered(entry, out reason))
            {
                reason = "Counter filter";
                return false;
            }

            // default behavior
            return IsNonIPMatch(entry, out reason);
        }

        /// <inheritdoc/>
        public bool IsFiltered(IPAddressRange range, out string reason)
        {
            // if we have a counter filter or a dns list and one of our dns servers is in the range, the range is not filtered
            if (counterFilter is not null && counterFilter.IsFiltered(range, out reason))
            {
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = "Counter filter";
                }
                return false;
            }
            else if (dnsList != null && dnsList.ContainsIPAddressRange(range))
            {
                reason = "Dns list";
                return false;
            }
            // if the set or ranges contains the range, it is filtered
            else if ((range.Single && set.Contains(range.Begin)) ||
                (set.Any(i => range.Contains(i)) || ranges.Any(r => r.Contains(range))))
            {
                reason = "IP list";
                return true;
            }

            reason = "Not found";
            return false;
        }

        /// <summary>
        /// Get entry without comment
        /// </summary>
        /// <param name="entry">Entry</param>
        /// <returns>Entry without comment</returns>
        public static string GetEntryWithoutComment(string entry)
        {
            string entryWithoutComment = entry;
            int pos = entryWithoutComment.IndexOf(ItemPiecesDelimiter);

            // if using new delimiter, remove it and everything after
            if (pos >= 0)
            {
                entryWithoutComment = entryWithoutComment[..pos];
            }
            // if using two or more of old delimiter, remove it and everything after
            else if (entryWithoutComment.Count(e => e == itemPiecesDelimiterLegacy) > 1)
            {
                pos = entryWithoutComment.IndexOf(itemPiecesDelimiterLegacy);
                entryWithoutComment = entryWithoutComment[..pos];
            }
            entryWithoutComment = entryWithoutComment.Trim();

            return entryWithoutComment;
        }

        /// <summary>
        /// Split entry on new delimiter. If it doesn't exist, legacy delimiter is used.
        /// </summary>
        /// <param name="entry">Entry</param>
        /// <returns>Pieces (always at least 3 items)</returns>
        public static string[] SplitEntry(string entry)
        {
            entry ??= string.Empty;
            string[] pieces = [];

            // split on new delimiter if we have it
            if (entry.Contains(ItemPiecesDelimiter))
            {
                pieces = entry.Split(ItemPiecesDelimiter, StringSplitOptions.TrimEntries);
            }

            // split on legacy delimiter if there's two or more
            else if (entry.Count(e => e == itemPiecesDelimiterLegacy) > 1)
            {
                pieces = entry.Split(itemPiecesDelimiterLegacy, StringSplitOptions.TrimEntries);
            }

            // the split should have at least 3 pieces, if not, return the original entry and two empty ones
            if (pieces.Length < 3)
            {
                return [entry, string.Empty, string.Empty];
            }

            return pieces;
        }

        /// <summary>
        /// Get all ip address ranges in the filter
        /// </summary>
        public IReadOnlyCollection<IPAddressRange> IPAddressRanges
        {
            get { return set.Select(b => new IPAddressRange(b)).Union(ranges).ToArray(); }
        }

        /// <summary>
        /// Get the original value
        /// </summary>
        public string Value => value;

        /// <summary>
        /// Get the regex filter
        /// </summary>
        public Regex Regex => regex;
    }
}
