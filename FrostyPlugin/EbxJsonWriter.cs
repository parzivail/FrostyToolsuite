using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers;

namespace Frosty.Core
{
	class ReferenceEqualityComparer : IEqualityComparer<object?>, IEqualityComparer
	{
		private ReferenceEqualityComparer()
		{
		}

		/// <summary>
		/// Gets the singleton <see cref="ReferenceEqualityComparer"/> instance.
		/// </summary>
		public static ReferenceEqualityComparer Instance { get; } = new ReferenceEqualityComparer();

		/// <summary>
		/// Determines whether two object references refer to the same object instance.
		/// </summary>
		/// <param name="x">The first object to compare.</param>
		/// <param name="y">The second object to compare.</param>
		/// <returns>
		/// <see langword="true"/> if both <paramref name="x"/> and <paramref name="y"/> refer to the same object instance
		/// or if both are <see langword="null"/>; otherwise, <see langword="false"/>.
		/// </returns>
		/// <remarks>
		/// This API is a wrapper around <see cref="object.ReferenceEquals(object?, object?)"/>.
		/// It is not necessarily equivalent to calling <see cref="object.Equals(object?, object?)"/>.
		/// </remarks>
		public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

		/// <summary>
		/// Returns a hash code for the specified object. The returned hash code is based on the object
		/// identity, not on the contents of the object.
		/// </summary>
		/// <param name="obj">The object for which to retrieve the hash code.</param>
		/// <returns>A hash code for the identity of <paramref name="obj"/>.</returns>
		/// <remarks>
		/// This API is a wrapper around <see cref="RuntimeHelpers.GetHashCode(object)"/>.
		/// It is not necessarily equivalent to calling <see cref="object.GetHashCode()"/>.
		/// </remarks>
		public int GetHashCode(object? obj)
		{
			// Depending on target framework, RuntimeHelpers.GetHashCode might not be annotated
			// with the proper nullability attribute. We'll suppress any warning that might
			// result.
			return RuntimeHelpers.GetHashCode(obj!);
		}
	}

	internal class EbxReferenceHandler : ReferenceHandler
	{
		public EbxReferenceHandler()
		{
			_rootedResolver = new EbxReferenceResolver();
		}

		private ReferenceResolver _rootedResolver;
		public override ReferenceResolver CreateResolver() => _rootedResolver;

		class EbxReferenceResolver : ReferenceResolver
		{
			private uint _referenceCount;
			private readonly Dictionary<string, object> _referenceIdToObjectMap = new Dictionary<string, object>();

			private readonly Dictionary<object, string> _objectToReferenceIdMap =
				new Dictionary<object, string>(ReferenceEqualityComparer.Instance);

			public override void AddReference(string referenceId, object value)
			{
				if (_referenceIdToObjectMap.ContainsKey(referenceId))
					throw new JsonException();
				_referenceIdToObjectMap[referenceId] = value;
			}

			public override string GetReference(object value, out bool alreadyExists)
			{
				if (_referenceCount > 10000)
				{
					alreadyExists = true;
					return "@MAX_REF_EXCEEDED";
				}

				if (_objectToReferenceIdMap.TryGetValue(value, out string referenceId))
				{
					alreadyExists = true;
				}
				else
				{
					_referenceCount++;
					referenceId = _referenceCount.ToString();
					_objectToReferenceIdMap.Add(value, referenceId);
					alreadyExists = false;
				}

				return referenceId;
			}

			public override object ResolveReference(string referenceId)
			{
				if (!_referenceIdToObjectMap.TryGetValue(referenceId, out object value))
				{
					throw new JsonException();
				}

				return value;
			}
		}
	}

	internal class EbxReferenceConverter : JsonConverter<PointerRef>
	{
		private readonly AssetManager assetManager;
		private readonly HashSet<string> interestedClasses;

		public EbxReferenceConverter(AssetManager assetManager, HashSet<string> interestedClasses)
		{
			this.assetManager = assetManager;
			this.interestedClasses = interestedClasses;
		}

		public override PointerRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			throw new NotSupportedException();
		}

		public override void Write(Utf8JsonWriter writer, PointerRef value, JsonSerializerOptions options)
		{
			switch (value.Type)
			{
				case PointerRefType.Internal:
					if (value.Internal == null || !interestedClasses.Contains(value.Internal.GetType().Name))
						return;
					JsonSerializer.Serialize(writer, value.Internal, options);
					break;
				case PointerRefType.External:
					var entry = assetManager.GetEbxEntry(value.External.FileGuid);
					var ebx = assetManager.GetEbx(entry);
					var data = ebx.GetObject(value.External.ClassGuid);
					if (data == null || !interestedClasses.Contains(data.GetType().Name))
						return;
					JsonSerializer.Serialize(writer, data, options);
					break;
				case PointerRefType.Null:
					writer.WriteNullValue();
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}
	}

	internal class EbxJsonWriter : IDisposable
	{
		private readonly FileStream fileStream;
		private readonly AssetManager assetManager;
		private readonly HashSet<string> propertyProfile;

		public EbxJsonWriter(FileStream fileStream, AssetManager assetManager, HashSet<string> propertyProfile)
		{
			this.fileStream = fileStream;
			this.assetManager = assetManager;
			this.propertyProfile = propertyProfile;
		}

		public void WriteObjects(IEnumerable<object> assetObjects)
		{
			var xinterestedProperties = new HashSet<string>()
			{
				
			};

			var interestedClasses = propertyProfile.Select(s => s.Split('.')[0]).ToHashSet();
			var foundEntries = new HashSet<string>(propertyProfile);
			
			var opts = new JsonSerializerOptions() {
				WriteIndented = true,
				// ReferenceHandler = new EbxReferenceHandler(),
				Converters = { new EbxReferenceConverter(assetManager, interestedClasses), new JsonStringEnumConverter() },
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
				TypeInfoResolver = new DefaultJsonTypeInfoResolver() {
					Modifiers = {
						typeInfo =>
						{
							foreach (var propertyInfo in typeInfo.Properties)
							{
								var propName = $"{typeInfo.Type.Name}.{propertyInfo.Name}";
								if (!propertyProfile.Contains(propName))
									propertyInfo.ShouldSerialize = static (obj, value) => false;
								else
									foundEntries.Remove(propName);
							}
						}
					}
				}
			};

			JsonSerializer.Serialize(fileStream, assetObjects.First(), opts);

			if (foundEntries.Count > 0)
				App.Logger.LogWarning($"Skipped missing properties: {string.Join(", ", foundEntries)}");
		}

		public void Dispose()
		{
			fileStream?.Dispose();
		}
	}
}