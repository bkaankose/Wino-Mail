﻿using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ApplicationConfiguration : IApplicationConfiguration
{
    public const string SharedFolderName = "WinoShared";

    public string ApplicationDataFolderPath { get; set; }
    public string PublisherSharedFolderPath { get; set; }
    public string ApplicationTempFolderPath { get; set; }

    public string ApplicationInsightsInstrumentationKey => "a5a07c2f-6e24-4055-bfc9-88e87eef873a";
}
