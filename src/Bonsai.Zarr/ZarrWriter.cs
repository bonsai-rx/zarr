using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Bonsai.Expressions;
using OpenCV.Net;

namespace Bonsai.Zarr
{
    /// <summary>
    /// Represents an operator that writes each element of the sequence to an array, or to a group
    /// of arrays, in a Zarr store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the element is a single value or an array of values, it is written to a single Zarr array
    /// with the specified name. Otherwise, each public field and property of the element is written
    /// to its own Zarr array, in a group with the specified name, with nested types flattened into
    /// dotted member names. The Zarr arrays in a group share their first dimension, so every member
    /// of an element must add the same number of rows.
    /// </para>
    /// <para>
    /// Without a buffer selector, each element adds a single row: a single value adds one row to a
    /// one-dimensional Zarr array, and an array of values adds one row to a two-dimensional Zarr
    /// array. With a buffer selector, each element adds as many rows as the selected buffer has
    /// samples: the values of an array are written one per row, and a matrix with one row per
    /// channel is written with one row per sample.
    /// </para>
    /// <para>
    /// Rows are appended directly to chunk files that hold the same bytes as a raw binary file of
    /// the same rows. The arrays are created with no rows when the first element arrives, and the
    /// number of rows in each array is written when the sequence terminates or the subscription
    /// is disposed.
    /// </para>
    /// </remarks>
    [DefaultProperty(nameof(Path))]
    [WorkflowElementCategory(ElementCategory.Sink)]
    [Description("Writes each element of the sequence to an array, or to a group of arrays, in a Zarr store.")]
    public class ZarrWriter : CombinatorExpressionBuilder, INamedElement
    {
        const string DataMemberSelectorEditor = "Bonsai.IO.Design.DataMemberSelectorEditor, Bonsai.System.Design";

        /// <summary>
        /// Initializes a new instance of the <see cref="ZarrWriter"/> class.
        /// </summary>
        public ZarrWriter()
            : base(minArguments: 1, maxArguments: 1)
        {
        }

        /// <summary>
        /// Gets or sets the path of the folder that contains the Zarr store.
        /// </summary>
        [Description("The path of the folder that contains the Zarr store.")]
        [Editor(DesignTypes.FolderNameEditor, DesignTypes.UITypeEditor)]
        public string? Path { get; set; }

        /// <summary>
        /// Gets or sets the name of the array or group in the store.
        /// </summary>
        /// <remarks>
        /// Group names are separated by slashes to nest the array or group. The name is also shown
        /// as the label of the operator.
        /// </remarks>
        [Description("The name of the array or group in the store. Separate group names with slashes to nest it.")]
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the inner properties that will be written for each element of the sequence.
        /// If no properties are specified, every public field and property is written.
        /// </summary>
        [Description("The inner properties that will be written for each element of the sequence. If no properties are specified, every public field and property is written.")]
        [Editor(DataMemberSelectorEditor, DesignTypes.UITypeEditor)]
        public string? ValueSelector { get; set; }

        /// <summary>
        /// Gets or sets the inner property that gives the number of samples in each element. If no
        /// property is specified, each element is written as a single row.
        /// </summary>
        /// <remarks>
        /// The property must be an array, whose length is the number of samples, or a matrix with
        /// one row per channel, whose number of columns is the number of samples. Every member that
        /// is written must have the same number of samples.
        /// </remarks>
        [Description("The inner property that gives the number of samples in each element. If no property is specified, each element is written as a single row.")]
        [Editor(DataMemberSelectorEditor, DesignTypes.UITypeEditor)]
        public string? BufferSelector { get; set; }

        /// <summary>
        /// Gets or sets the optional number of rows stored in each chunk file. If no value is
        /// specified, each array is stored in a single file.
        /// </summary>
        [Description("The optional number of rows stored in each chunk file. If no value is specified, each array is stored in a single file.")]
        public int? ChunkLength { get; set; }

        /// <summary>
        /// Gets or sets a value specifying whether to replace an existing array or group with the
        /// same name.
        /// </summary>
        /// <remarks>
        /// <see langword="true"/> to replace an existing array or group with the same name, together
        /// with everything it holds; otherwise, an error is raised if it already exists.
        /// </remarks>
        [Description("True to replace an existing array or group with the same name; otherwise, an error is raised if it already exists.")]
        public bool Overwrite { get; set; }

        /// <inheritdoc/>
        protected override Expression BuildCombinator(IEnumerable<Expression> arguments)
        {
            var source = arguments.First();
            var parameterType = source.Type.GetGenericArguments()[0];
            var parameter = Expression.Parameter(parameterType, "input");
            var bufferSelector = BufferSelector;
            var buffered = !string.IsNullOrWhiteSpace(bufferSelector);

            var members = new List<Expression>();
            var names = new HashSet<string>();
            foreach (var selected in ExpressionHelper.SelectMembers(parameter, ValueSelector!))
            {
                var name = GetMemberName(selected);
                var count = members.Count;
                CreateMembers(parameter, selected, name, buffered, members, new List<Type>());
                if (members.Count == count)
                {
                    throw new NotSupportedException($"{GetDisplayName(name)} of type '{selected.Type}' has no members that can be written.");
                }
            }

            foreach (var name in members.Select(member => (string)((ConstantExpression)((NewExpression)member).Arguments[0]).Value!))
            {
                if (!names.Add(name))
                {
                    throw new InvalidOperationException($"{GetDisplayName(name)} is selected more than once.");
                }
            }

            if (names.Contains(string.Empty) && names.Count > 1)
            {
                throw new InvalidOperationException("The element itself cannot be written together with any of its members.");
            }

            var memberType = typeof(ZarrMember<>).MakeGenericType(parameterType);
            var bufferLengthType = typeof(Func<,>).MakeGenericType(parameterType, typeof(int));
            var bufferLength = buffered
                ? (Expression)Expression.Lambda(bufferLengthType, GetBufferLength(ExpressionHelper.MemberAccess(parameter, bufferSelector!)), parameter)
                : Expression.Constant(null, bufferLengthType);
            return Expression.Call(
                Expression.Constant(this),
                nameof(Process),
                new[] { parameterType },
                source,
                Expression.NewArrayInit(memberType, members),
                bufferLength);
        }

        static string GetMemberName(Expression expression)
        {
            var path = expression.ToString();
            var separator = path.IndexOf(ExpressionHelper.MemberSeparator, StringComparison.Ordinal);
            return separator < 0 ? string.Empty : path.Substring(separator + 1);
        }

        static string GetDisplayName(string name)
        {
            return name.Length == 0 ? "The element" : $"The member '{name}'";
        }

        static Expression GetBufferLength(Expression buffer)
        {
            if (buffer.Type.IsArray && buffer.Type.GetArrayRank() == 1)
            {
                return Expression.ArrayLength(buffer);
            }

            if (buffer.Type == typeof(Mat))
            {
                return Expression.Property(buffer, nameof(Mat.Cols));
            }

            throw new InvalidOperationException($"The buffer of type '{buffer.Type}' must be an array or a matrix.");
        }

        static void CreateMembers(
            ParameterExpression parameter,
            Expression expression,
            string name,
            bool buffered,
            List<Expression> members,
            List<Type> ancestors)
        {
            var type = expression.Type;
            if (type.IsEnum)
            {
                type = Enum.GetUnderlyingType(type);
                expression = Expression.Convert(expression, type);
            }

            if (ZarrDataType.IsSupported(type))
            {
                if (buffered)
                {
                    throw new InvalidOperationException($"{GetDisplayName(name)} holds a single value per element, which cannot be written alongside a buffer of samples.");
                }

                members.Add(CreateMember(typeof(ZarrScalarMember<,>), parameter, expression, name, type));
            }
            else if (type.IsArray)
            {
                var elementType = type.GetElementType()!;
                if (type.GetArrayRank() != 1 || !ZarrDataType.IsSupported(elementType))
                {
                    throw new NotSupportedException($"{GetDisplayName(name)} of type '{type}' is not supported. Only one-dimensional arrays of numeric or boolean values can be written.");
                }

                var memberType = buffered ? typeof(ZarrArrayBufferMember<,>) : typeof(ZarrArrayRowMember<,>);
                members.Add(CreateMember(memberType, parameter, expression, name, elementType));
            }
            else if (type == typeof(Mat))
            {
                if (!buffered)
                {
                    throw new NotSupportedException($"{GetDisplayName(name)} is a matrix, which can only be written as a buffer of samples. Specify a buffer selector.");
                }

                var memberType = typeof(ZarrMatBufferMember<>).MakeGenericType(parameter.Type);
                var selector = Expression.Lambda(typeof(Func<,>).MakeGenericType(parameter.Type, type), expression, parameter);
                members.Add(Expression.New(memberType.GetConstructors()[0], Expression.Constant(name), selector));
            }
            else if (type.IsPrimitive || type.IsPointer || type == typeof(string) || type == typeof(decimal) ||
                     type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
                     Nullable.GetUnderlyingType(type) != null || typeof(IEnumerable).IsAssignableFrom(type))
            {
                throw new NotSupportedException($"{GetDisplayName(name)} of type '{type}' has no corresponding Zarr data type.");
            }
            else if (!ancestors.Contains(type))
            {
                ancestors.Add(type);
                foreach (var member in GetDataMembers(type))
                {
                    var memberName = name.Length == 0 ? member.Name : name + ExpressionHelper.MemberSeparator + member.Name;
                    CreateMembers(parameter, Expression.MakeMemberAccess(expression, member), memberName, buffered, members, ancestors);
                }

                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }

        static Expression CreateMember(Type memberType, ParameterExpression parameter, Expression expression, string name, Type elementType)
        {
            var constructedType = memberType.MakeGenericType(parameter.Type, elementType);
            var selector = Expression.Lambda(typeof(Func<,>).MakeGenericType(parameter.Type, expression.Type), expression, parameter);
            return Expression.New(constructedType.GetConstructors()[0], Expression.Constant(name), selector);
        }

        static IEnumerable<MemberInfo> GetDataMembers(Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
            var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead && property.GetIndexParameters().Length == 0);
            return Enumerable.Concat<MemberInfo>(fields, properties)
                .Where(member => !member.IsDefined(typeof(XmlIgnoreAttribute), true))
                .OrderBy(member => member.MetadataToken);
        }

        IObservable<TSource> Process<TSource>(
            IObservable<TSource> source,
            ZarrMember<TSource>[] members,
            Func<TSource, int>? getBufferLength)
        {
            return Observable.Create<TSource>(observer =>
            {
                var storePath = Path;
                if (string.IsNullOrEmpty(storePath))
                {
                    throw new InvalidOperationException("A store path must be specified.");
                }

                var path = string.Join("/", ZarrStore.GetNodeNames(Name));
                var writer = new ZarrFrameWriter<TSource>(storePath!, path, members, getBufferLength, ChunkLength, Overwrite);
                var gate = new object();
                var disposed = false;
                var subscription = source.Do(
                    value =>
                    {
                        lock (gate)
                        {
                            if (disposed) return;
                            writer.Write(value);
                        }
                    },
                    () =>
                    {
                        lock (gate)
                        {
                            disposed = true;
                            writer.Dispose();
                        }
                    })
                    .SubscribeSafe(observer);

                return Disposable.Create(() =>
                {
                    subscription.Dispose();
                    lock (gate)
                    {
                        disposed = true;
                        writer.Dispose();
                    }
                });
            });
        }
    }
}
