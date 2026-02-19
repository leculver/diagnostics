// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;

namespace Microsoft.Diagnostics.Tools.Trace
{
    /// <summary>
    /// Loads dotnet-trace profile definitions from JSON files (.dtp extension).
    /// Search order: current directory, then ~/.dotnet/dotnet-trace/profiles/.
    /// </summary>
    internal static class ProfileFileReader
    {
        private const string ProfileFileExtension = ".dtp";

        /// <summary>
        /// Attempts to load a profile by name from known search directories.
        /// </summary>
        public static Profile TryLoadProfileByName(string profileName)
        {
            foreach (string directory in GetSearchDirectories())
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                string filePath = Path.Combine(directory, profileName + ProfileFileExtension);
                if (File.Exists(filePath))
                {
                    return LoadProfileFromFile(filePath);
                }
            }

            return null;
        }

        /// <summary>
        /// Enumerates all profile files found in the search directories.
        /// </summary>
        public static IEnumerable<Profile> LoadAllFileProfiles()
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

            foreach (string directory in GetSearchDirectories())
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (string filePath in Directory.EnumerateFiles(directory, "*" + ProfileFileExtension))
                {
                    Profile profile;
                    try
                    {
                        profile = LoadProfileFromFile(filePath);
                    }
                    catch (Exception)
                    {
                        // Skip malformed files during enumeration
                        continue;
                    }

                    if (seen.Add(profile.Name))
                    {
                        yield return profile;
                    }
                }
            }
        }

        /// <summary>
        /// Returns the user-level profile directory path.
        /// </summary>
        public static string GetUserProfileDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".dotnet", "dotnet-trace", "profiles");
        }

        private static IEnumerable<string> GetSearchDirectories()
        {
            yield return Directory.GetCurrentDirectory();
            yield return GetUserProfileDirectory();
        }

        private static Profile LoadProfileFromFile(string filePath)
        {
            string json = File.ReadAllText(filePath);

            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            JsonElement root = doc.RootElement;

            string name = root.TryGetProperty("name", out JsonElement nameElement)
                ? nameElement.GetString()
                : Path.GetFileNameWithoutExtension(filePath);

            string description = root.TryGetProperty("description", out JsonElement descElement)
                ? descElement.GetString()
                : string.Empty;

            List<EventPipeProvider> providers = new();
            if (root.TryGetProperty("providers", out JsonElement providersElement) &&
                providersElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement providerElement in providersElement.EnumerateArray())
                {
                    providers.Add(ParseProvider(providerElement));
                }
            }

            return new Profile(name, providers, description);
        }

        private static EventPipeProvider ParseProvider(JsonElement element)
        {
            string name = element.GetProperty("name").GetString();

            long keywords = 0;
            if (element.TryGetProperty("keywords", out JsonElement kwElement))
            {
                string kwStr = kwElement.GetString();
                if (kwStr != null)
                {
                    if (kwStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        keywords = Convert.ToInt64(kwStr.Substring(2), 16);
                    }
                    else
                    {
                        keywords = long.Parse(kwStr);
                    }
                }
            }

            EventLevel eventLevel = EventLevel.Informational;
            if (element.TryGetProperty("eventLevel", out JsonElement levelElement))
            {
                string levelStr = levelElement.GetString();
                if (levelStr != null)
                {
                    eventLevel = levelStr.ToLowerInvariant() switch
                    {
                        "critical" => EventLevel.Critical,
                        "error" => EventLevel.Error,
                        "informational" => EventLevel.Informational,
                        "logalways" => EventLevel.LogAlways,
                        "verbose" => EventLevel.Verbose,
                        "warning" => EventLevel.Warning,
                        _ => int.TryParse(levelStr, out int parsed)
                            ? (EventLevel)parsed
                            : throw new JsonException($"Unknown eventLevel: {levelStr}")
                    };
                }
            }

            Dictionary<string, string> arguments = null;
            if (element.TryGetProperty("arguments", out JsonElement argsElement) &&
                argsElement.ValueKind == JsonValueKind.Object)
            {
                arguments = new Dictionary<string, string>();
                foreach (JsonProperty prop in argsElement.EnumerateObject())
                {
                    arguments[prop.Name] = prop.Value.GetString();
                }
            }

            return new EventPipeProvider(name, eventLevel, keywords, arguments);
        }
    }
}
