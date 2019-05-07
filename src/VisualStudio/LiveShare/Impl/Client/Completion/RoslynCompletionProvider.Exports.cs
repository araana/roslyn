﻿//
//  Copyright (c) Microsoft Corporation. All rights reserved.
//

using System.Composition;
using Microsoft.Cascade.Common;
using Microsoft.CodeAnalysis.Completion;

namespace Microsoft.VisualStudio.LanguageServices.LiveShare.Client
{
    [ExportCompletionProvider("CSharpLspCompletionProvider", StringConstants.CSharpLspLanguageName), Shared]
    internal class CSharpLspCompletionProvider : RoslynCompletionProvider
    {
        [ImportingConstructor]
        public CSharpLspCompletionProvider(RoslynLSPClientServiceFactory roslynLSPClientServiceFactory, IVsConfigurationSettings configurationSettings)
            : base(roslynLSPClientServiceFactory, configurationSettings)
        {
        }
    }

    [ExportCompletionProvider("VBLspCompletionProvider", StringConstants.VBLspLanguageName), Shared]
    internal class VBLspCompletionProvider : RoslynCompletionProvider
    {
        [ImportingConstructor]
        public VBLspCompletionProvider(RoslynLSPClientServiceFactory roslynLSPClientServiceFactory, IVsConfigurationSettings configurationSettings)
            : base(roslynLSPClientServiceFactory, configurationSettings)
        {
        }
    }

#if !VS_16_0
    [ExportCompletionProvider("TypeScriptLspCompletionProvider", StringConstants.TypeScriptLanguageName), Shared]
    internal class TypeScriptLspCompletionProvider : RoslynCompletionProvider
    {
        [ImportingConstructor]
        public TypeScriptLspCompletionProvider(RoslynLSPClientServiceFactory roslynLSPClientServiceFactory, IVsConfigurationSettings configurationSettings)
            : base(roslynLSPClientServiceFactory, configurationSettings)
        {
        }
    }
#endif
}