﻿using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Reflection;
using System.Collections.Generic;

using BrackeysBot.Core.Models;

namespace BrackeysBot.Services
{
    public class CustomCommandService : BrackeysBotService
    {
        private List<CustomCommand> _commands;

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            Converters = { new CustomCommandFeatureConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        };

        private const string _commandFilePath = "commands.json";
        private const StringComparison _comparison = StringComparison.InvariantCultureIgnoreCase;

        public CustomCommandService()
        {
            LoadCustomCommands();
        }

        public IReadOnlyCollection<CustomCommand> GetCommands()
            => _commands.AsReadOnly();

        public bool TryGetCommand(string name, out CustomCommand command)
        {
            command = GetCommandByName(name);
            return command != null;
        }

        public CustomCommand GetCommandByName(string name)
            => _commands.FirstOrDefault(c => c.Name.Equals(name, _comparison));
        public bool ContainsCommand(string name)
            => _commands.Any(c => c.Name.Equals(name, _comparison));

        public CustomCommand CreateCommand(string name)
        {
            var command = new CustomCommand(name);
            _commands.Add(command);

            SaveCustomCommands();

            return command;
        }
        public void RemoveCommand(string name)
        {
            _commands.RemoveAll(c => c.Name.Equals(name, _comparison));
        }

        public CustomCommandFeature CreateFeature(string name, string arguments)
        {
            Type match = GetFeatureTypes()
                .FirstOrDefault(t => CustomCommandFeature.GetName(t).Equals(name, _comparison));

            if (match == null)
                throw new Exception($"The feature '{name}' was not found.");

            var feature = Activator.CreateInstance(match) as CustomCommandFeature;
            feature.FillArguments(arguments);

            return feature;
        }
        public void RemoveFeatureFromCommand(CustomCommand command, string feature)
        {
            command.Features.RemoveAll(f => f.Name.Equals(feature, _comparison));
        }

        public void SaveCustomCommands()
        {
            string json = JsonSerializer.Serialize(_commands, _jsonOptions);
            File.WriteAllText(_commandFilePath, json);
        }
        public void LoadCustomCommands()
        {
            if (!File.Exists(_commandFilePath))
            {
                _commands = new List<CustomCommand>();
                SaveCustomCommands();
                return;
            }

            string json = File.ReadAllText(_commandFilePath);
            _commands = JsonSerializer.Deserialize<List<CustomCommand>>(json, _jsonOptions);
        }

        public FeatureInfo[] GetFeatureInfos()
            => GetFeatureTypes()
                .Select(feature => new FeatureInfo { Name = CustomCommandFeature.GetName(feature), Summary = CustomCommandFeature.GetSummary(feature) })
                .ToArray();
        private static IEnumerable<Type> GetFeatureTypes()
            => typeof(CustomCommandFeature).Assembly.GetTypes()
                .Where(t => !t.IsAbstract && typeof(CustomCommandFeature).IsAssignableFrom(t));
    }
}
