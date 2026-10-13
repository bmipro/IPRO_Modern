using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IPRO.DataAccess;

// 573. A record that arrives bound from a form is the customer's words for its plain fields and
// nothing more. The binder, though, fills whatever the request names: the record's key, and any
// related record it can reach (Client.PortalDocuments[0].BlobUrl, AgentUser.Email). Adding such an
// object to the database whole writes all of that as new rows: a document row pointing at somebody
// else's file, an account nobody signed up for.
//
// Clean() is called on a posted record before it is added. It leaves the plain fields, empties
// every relation and resets the key, so what is added is one row and only that row. Anything that
// should be related (a client's categories) is attached afterwards, from the database, by the code.
public static class PostedRecord
{
    public static T Clean<T>(IPRODbContext db, T posted) where T : class
    {
        var type = db.Model.FindEntityType(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T).Name} is not a stored record.");

        foreach (var navigation in type.GetNavigations().Cast<INavigationBase>().Concat(type.GetSkipNavigations()))
        {
            var property = navigation.PropertyInfo;
            if (property == null || !property.CanWrite) continue;
            var empty = navigation.IsCollection ? typeof(List<>).MakeGenericType(navigation.TargetEntityType.ClrType) : null;
            property.SetValue(posted, empty != null && property.PropertyType.IsAssignableFrom(empty) ? Activator.CreateInstance(empty) : null);
        }

        foreach (var key in type.FindPrimaryKey()?.Properties ?? Array.Empty<IProperty>())
        {
            var property = key.PropertyInfo;
            if (property == null || !property.CanWrite) continue;
            property.SetValue(posted, property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null);
        }

        return posted;
    }
}
