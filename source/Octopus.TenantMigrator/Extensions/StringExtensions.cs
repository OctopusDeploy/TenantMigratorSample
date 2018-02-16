using System;
using System.Collections.Generic;

// ReSharper disable once CheckNamespace
namespace Octopus.TenantMigrator.Extensions
{
    public static class StringExtensions
    {
        public static string CommaSeperate(this IEnumerable<object> items) => string.Join(", ", items);
        public static string NewLineSeperate(this IEnumerable<object> items) => string.Join(Environment.NewLine, items);
    }
}