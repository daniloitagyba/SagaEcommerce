using Catalog.Service.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;

namespace Catalog.Service.Data;

public static class MongoClassMaps
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        BsonClassMap.RegisterClassMap<Product>(classMap =>
        {
            classMap.AutoMap();
            classMap.MapIdProperty(product => product.Id)
                .SetSerializer(new StringSerializer(BsonType.ObjectId))
                .SetIdGenerator(StringObjectIdGenerator.Instance);
        });

        _registered = true;
    }
}
