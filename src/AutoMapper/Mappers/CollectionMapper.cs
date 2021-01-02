using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Collections.Specialized;
using System.Linq;
namespace AutoMapper.Internal.Mappers
{
    using Execution;
    using static Execution.ExpressionBuilder;
    using static Expression;
    using static ReflectionHelper;
    public class CollectionMapper : IObjectMapperInfo
    {
        public TypePair GetAssociatedTypes(in TypePair context) => new TypePair(GetElementType(context.SourceType), GetElementType(context.DestinationType));
        public bool IsMatch(in TypePair context) => context.SourceType.IsCollection() && context.DestinationType.IsCollection();
        public Expression MapExpression(IGlobalConfiguration configurationProvider, ProfileMap profileMap, MemberMap memberMap, Expression sourceExpression, Expression destExpression)
        {
            var destinationType = destExpression.Type;
            if (destinationType.IsArray)
            {
                return ArrayMapper.MapToArray(configurationProvider, profileMap, sourceExpression, destinationType);
            }
            if (destinationType.IsGenericType(typeof(ReadOnlyCollection<>)))
            {
                return MapReadOnlyCollection(typeof(List<>), typeof(ReadOnlyCollection<>));
            }
            if (destinationType.IsGenericType(typeof(ReadOnlyDictionary<,>)) || destinationType.IsGenericType(typeof(IReadOnlyDictionary<,>)))
            {
                return MapReadOnlyCollection(typeof(Dictionary<,>), typeof(ReadOnlyDictionary<,>));
            }
            if (destinationType == sourceExpression.Type && destinationType.Name == nameof(NameValueCollection))
            {
                return CreateNameValueCollection(sourceExpression);
            }
            return MapCollectionCore(destExpression);
            Expression MapReadOnlyCollection(Type genericCollectionType, Type genericReadOnlyCollectionType)
            {
                var destinationTypeArguments = destinationType.GenericTypeArguments;
                var closedCollectionType = genericCollectionType.MakeGenericType(destinationTypeArguments);
                var dict = MapCollectionCore(Default(closedCollectionType));
                var readOnlyClosedType = destinationType.IsInterface ? genericReadOnlyCollectionType.MakeGenericType(destinationTypeArguments) : destinationType;
                return New(readOnlyClosedType.GetConstructors()[0], dict);
            }
            Expression MapCollectionCore(Expression destExpression)
            {
                var destinationType = destExpression.Type;
                MethodInfo addMethod;
                bool isIList;
                Type destinationCollectionType, destinationElementType;
                GetDestinationType();
                var passedDestination = Variable(destExpression.Type, "passedDestination");
                var newExpression = Variable(passedDestination.Type, "collectionDestination");
                var sourceElementType = sourceExpression.Type.GetICollectionType()?.GenericTypeArguments[0] ?? GetEnumerableElementType(sourceExpression.Type);
                var itemParam = Parameter(sourceElementType, "item");
                var itemExpr = configurationProvider.MapExpression(profileMap, new TypePair(sourceElementType, destinationElementType), itemParam);
                Expression destination, assignNewExpression;
                UseDestinationValue();
                var addItems = ForEach(itemParam, sourceExpression, Call(destination, addMethod, itemExpr));
                var overMaxDepth = OverMaxDepth(memberMap?.TypeMap);
                if (overMaxDepth != null)
                {
                    addItems = Condition(overMaxDepth, ExpressionBuilder.Empty, addItems);
                }
                var clearMethod = isIList ? IListClear : destinationCollectionType.GetMethod("Clear");
                var checkNull = Block(new[] { newExpression, passedDestination },
                        Assign(passedDestination, destExpression),
                        assignNewExpression,
                        Call(destination, clearMethod),
                        addItems,
                        destination);
                if (memberMap != null)
                {
                    return checkNull;
                }
                return CheckContext();
                void GetDestinationType()
                {
                    destinationCollectionType = destinationType.GetICollectionType();
                    destinationElementType = destinationCollectionType?.GenericTypeArguments[0] ?? GetEnumerableElementType(destinationType);
                    if (destinationCollectionType == null && destinationType.IsInterface)
                    {
                        destinationCollectionType = typeof(ICollection<>).MakeGenericType(destinationElementType);
                        destExpression = ToType(destExpression, destinationCollectionType);
                    }
                    if (destinationCollectionType == null)
                    {
                        destinationCollectionType = typeof(IList);
                        addMethod = IListAdd;
                        isIList = true;
                    }
                    else
                    {
                        isIList = destExpression.Type.IsListType();
                        addMethod = destinationCollectionType.GetMethod("Add");
                    }
                }
                void UseDestinationValue()
                {
                    if (memberMap is { UseDestinationValue: true })
                    {
                        destination = passedDestination;
                        assignNewExpression = ExpressionBuilder.Empty;
                    }
                    else
                    {
                        destination = newExpression;
                        var createInstance = ObjectFactory.GenerateConstructorExpression(passedDestination.Type);
                        var shouldCreateDestination = ReferenceEqual(passedDestination, Null);
                        if (memberMap is { CanBeSet: true })
                        {
                            var isReadOnly = isIList ? Property(passedDestination, IListIsReadOnly) : ExpressionBuilder.Property(ToType(passedDestination, destinationCollectionType), "IsReadOnly");
                            shouldCreateDestination = OrElse(shouldCreateDestination, isReadOnly);
                        }
                        assignNewExpression = Assign(newExpression, Condition(shouldCreateDestination, ToType(createInstance, passedDestination.Type), passedDestination));
                    }
                }
                Expression CheckContext()
                {
                    var elementTypeMap = configurationProvider.ResolveTypeMap(sourceElementType, destinationElementType);
                    if (elementTypeMap == null)
                    {
                        return checkNull;
                    }
                    var checkContext = ExpressionBuilder.CheckContext(elementTypeMap);
                    if (checkContext == null)
                    {
                        return checkNull;
                    }
                    return Block(checkContext, checkNull);
                }
            }
        }
        private static Expression CreateNameValueCollection(Expression sourceExpression) =>
            New(typeof(NameValueCollection).GetConstructor(new[] { typeof(NameValueCollection) }), sourceExpression);
        static class ArrayMapper
        {
            private static readonly MethodInfo CopyToMethod = typeof(Array).GetMethod("CopyTo", new[] { typeof(Array), typeof(int) });
            private static readonly MethodInfo CountMethod = typeof(Enumerable).StaticGenericMethod("Count", parametersCount: 1);
            private static readonly MethodInfo MapMultidimensionalMethod = typeof(ArrayMapper).GetStaticMethod(nameof(MapMultidimensional));
            private static Array MapMultidimensional(Array source, Type destinationElementType, ResolutionContext context)
            {
                var sourceElementType = source.GetType().GetElementType();
                var destinationArray = Array.CreateInstance(destinationElementType, Enumerable.Range(0, source.Rank).Select(source.GetLength).ToArray());
                var filler = new MultidimensionalArrayFiller(destinationArray);
                foreach (var item in source)
                {
                    filler.NewValue(context.Map(item, null, sourceElementType, destinationElementType, null));
                }
                return destinationArray;
            }
            public static Expression MapToArray(IGlobalConfiguration configurationProvider, ProfileMap profileMap, Expression sourceExpression, Type destinationType)
            {
                var destinationElementType = destinationType.GetElementType();
                if (destinationType.GetArrayRank() > 1)
                {
                    return Call(MapMultidimensionalMethod, sourceExpression, Constant(destinationElementType), ContextParameter);
                }
                var sourceType = sourceExpression.Type;
                Type sourceElementType = null;
                Expression createDestination;
                var destination = Parameter(destinationType, "destinationArray");
                if (sourceType.IsArray)
                {
                    var mapFromArray = MapFromArray();
                    if (mapFromArray != null)
                    {
                        return mapFromArray;
                    }
                }
                else
                {
                    var mapFromICollection = MapFromICollection();
                    if (mapFromICollection != null)
                    {
                        return mapFromICollection;
                    }
                    sourceElementType ??= GetEnumerableElementType(sourceExpression.Type);
                    var count = Call(CountMethod.MakeGenericMethod(sourceElementType), sourceExpression);
                    createDestination = Assign(destination, NewArrayBounds(destinationElementType, count));
                }
                var itemParam = Parameter(sourceElementType, "sourceItem");
                var itemExpr = configurationProvider.MapExpression(profileMap, new TypePair(sourceElementType, destinationElementType), itemParam);
                var indexParam = Parameter(typeof(int), "destinationArrayIndex");
                var setItem = Assign(ArrayAccess(destination, PostIncrementAssign(indexParam)), itemExpr);
                return Block(new[] { destination, indexParam },
                    createDestination,
                    Assign(indexParam, Zero),
                    ForEach(itemParam, sourceExpression, setItem),
                    destination);
                Expression MapFromArray()
                {
                    sourceElementType = sourceType.GetElementType();
                    createDestination = Assign(destination, NewArrayBounds(destinationElementType, ArrayLength(sourceExpression)));
                    if (!destinationElementType.IsAssignableFrom(sourceElementType) || 
                        configurationProvider.FindTypeMapFor(sourceElementType, destinationElementType) != null)
                    {
                        return null;
                    }
                    return Block(new[] { destination },
                        createDestination,
                        Call(sourceExpression, CopyToMethod, destination, Zero),
                        destination);
                }
                Expression MapFromICollection()
                {
                    var collectionType = sourceType.GetICollectionType();
                    if (collectionType == null || (sourceElementType = collectionType.GenericTypeArguments[0]) != destinationElementType ||
                        configurationProvider.FindTypeMapFor(sourceElementType, destinationElementType) != null)
                    {
                        return null;
                    }
                    var sourceICollection = Variable(collectionType, "sourceICollection");
                    var count = ExpressionBuilder.Property(sourceICollection, "Count");
                    return Block(new[] { destination, sourceICollection },
                        Assign(sourceICollection, ToType(sourceExpression, collectionType)),
                        Assign(destination, NewArrayBounds(destinationElementType, count)),
                        Call(sourceICollection, "CopyTo", destination, Zero),
                        destination);
                }
            }
        }
    }
    public class MultidimensionalArrayFiller
    {
        private readonly int[] _indices;
        private readonly Array _destination;
        public MultidimensionalArrayFiller(Array destination)
        {
            _indices = new int[destination.Rank];
            _destination = destination;
        }
        public void NewValue(object value)
        {
            var dimension = _destination.Rank - 1;
            var changedDimension = false;
            while (_indices[dimension] == _destination.GetLength(dimension))
            {
                _indices[dimension] = 0;
                dimension--;
                if (dimension < 0)
                {
                    throw new InvalidOperationException("Not enough room in destination array " + _destination);
                }
                _indices[dimension]++;
                changedDimension = true;
            }
            _destination.SetValue(value, _indices);
            if (changedDimension)
            {
                _indices[dimension + 1]++;
            }
            else
            {
                _indices[dimension]++;
            }
        }
    }
}