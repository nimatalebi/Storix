using System.Collections;
using System.Reflection;
using NT.Storix.Core.Models;

namespace NT.Storix.Core.Security;

/// <summary>Walks an object graph and rewrites every string property marked with <see cref="SecretAttribute"/>.</summary>
public static class SecretWalker
{
    public static void Transform(object? root, Func<string?, string?> transform)
    {
        Visit(root, transform, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    private static void Visit(object? node, Func<string?, string?> transform, HashSet<object> visited)
    {
        if (node is null || node is string || node.GetType().IsPrimitive || node.GetType().IsEnum || !visited.Add(node))
        {
            return;
        }

        if (node is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                Visit(item, transform, visited);
            }

            return;
        }

        var type = node.GetType();
        if (type.Namespace is null || !type.Namespace.StartsWith("NT.", StringComparison.Ordinal))
        {
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (property.PropertyType == typeof(string))
            {
                if (property.CanWrite && property.IsDefined(typeof(SecretAttribute)))
                {
                    property.SetValue(node, transform((string?)property.GetValue(node)));
                }

                continue;
            }

            if (property.CanWrite)
            {
                Visit(property.GetValue(node), transform, visited);
            }
        }
    }
}
