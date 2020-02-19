// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;

namespace Agent.Sdk
{
    public interface ITraceWriter
    {
        void Info(string message);
        void Verbose(string message);
        void Error(Exception ex);
        void Error(string message);
        void Warning(string message);
        void Output(string message);
        void Command(string message);
        void Debug(string message);
    }
}
