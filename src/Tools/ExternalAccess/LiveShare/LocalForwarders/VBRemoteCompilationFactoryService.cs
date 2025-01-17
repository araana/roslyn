﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Composition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;

namespace Microsoft.CodeAnalysis.ExternalAccess.LiveShare.LocalForwarders
{
    [ExportLanguageServiceFactory(typeof(ICompilationFactoryService), StringConstants.VBLspLanguageName), Shared]
    internal class VBRemoteCompilationFactoryService : ILanguageServiceFactory
    {
        public ILanguageService CreateLanguageService(HostLanguageServices languageServices)
        {
            return languageServices.GetOriginalLanguageService<ICompilationFactoryService>();
        }
    }
}
