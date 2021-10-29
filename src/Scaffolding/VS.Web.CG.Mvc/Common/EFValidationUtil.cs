// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.DotNet.Scaffolding.Shared.ProjectModel;

namespace Microsoft.VisualStudio.Web.CodeGenerators.Mvc
{
    internal static class EFValidationUtil
    {
        const string EfDesignPackageName = "Microsoft.EntityFrameworkCore.Design";
        const string SqlServerPackageName = "Microsoft.EntityFrameworkCore.SqlServer";

        internal static void ValidateEFDependencies(IEnumerable<DependencyDescription> dependencies, bool useSqlite, bool calledFromCommandline)
        {
            var isEFDesignPackagePresent = dependencies
                .Any(package => package.Name.Equals(EfDesignPackageName, StringComparison.OrdinalIgnoreCase));

            if (!isEFDesignPackagePresent && calledFromCommandline)
            {
                 throw new InvalidOperationException(
                    string.Format(MessageStrings.InstallEfPackages, $"{EfDesignPackageName}"));
            }
            if (!useSqlite)
            {
                ValidateSqlServerDependency(dependencies);
            } 
        }

        internal static void ValidateSqlServerDependency(IEnumerable<DependencyDescription> dependencies)
        { 
            var isSqlServerPackagePresent = dependencies
                .Any(package => package.Name.Equals(SqlServerPackageName, StringComparison.OrdinalIgnoreCase));
            
            if (!isSqlServerPackagePresent) 
            {
                throw new InvalidOperationException(
                    string.Format(MessageStrings.InstallSqlPackage, $"{SqlServerPackageName}."));
            }
        }
    }
}