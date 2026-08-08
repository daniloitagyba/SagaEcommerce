using Catalog.Service.Domain;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Bson.Serialization.Serializers;

namespace Catalog.Service.Data;

// Keeps Catalog.Service.Domain free of any MongoDB.Bson
// dependency - the wire representation (Id as an ObjectId) is a Data-layer
// concern, not something the domain model should know about. Must run once,
// before the first IMongoCollection<Product> is used.
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
