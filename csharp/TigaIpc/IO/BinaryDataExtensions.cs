using System.IO.Compression;
using MessagePack;
using Newtonsoft.Json;

namespace TigaIpc.IO
{
    /// <summary>
    /// Provide BinaryData extension methods to unify serialization and deserialization operations
    /// </summary>
    public static class BinaryDataExtensions
    {
        /// <summary>
        /// Serialize an object to MessagePack format BinaryData
        /// </summary>
        /// <typeparam name="T">Object type</typeparam>
        /// <param name="obj">Object to serialize</param>
        /// <param name="compress">Enable compression</param>
        /// <param name="compressionThreshold">Compression threshold (bytes), default is 256</param>
        /// <returns>BinaryData containing serialized data</returns>
        public static BinaryData FromObjectAsMessagePack<T>(
            T obj,
            bool compress = false,
            int compressionThreshold = 256
        )
        {
            var bytes = MessagePackSerializer.Serialize(obj, MessagePackOptions.Instance);

            if (compress && bytes.Length > compressionThreshold)
            {
                bytes = CompressBytes(bytes);
                return BinaryData.FromBytes(bytes, "application/x-msgpack-compressed");
            }

            return BinaryData.FromBytes(bytes, "application/x-msgpack");
        }

        /// <summary>
        /// Deserialize a BinaryData to an object of the specified type (MessagePack format)
        /// </summary>
        /// <typeparam name="T">Target object type</typeparam>
        /// <param name="data">BinaryData to deserialize</param>
        /// <returns>Deserialized object</returns>
        public static T? ToObjectFromMessagePack<T>(this BinaryData data)
        {
            if (data.MediaType == "application/x-msgpack-compressed")
            {
                var decompressedBytes = DecompressBytes(data.ToArray());
                return MessagePackSerializer.Deserialize<T>(
                    decompressedBytes,
                    MessagePackOptions.Instance
                );
            }

            return MessagePackSerializer.Deserialize<T>(data, MessagePackOptions.Instance);
        }

        /// <summary>
        /// Serialize an object to JSON format BinaryData
        /// </summary>
        /// <typeparam name="T">Object type</typeparam>
        /// <param name="obj">Object to serialize</param>
        /// <returns>BinaryData containing serialized data</returns>
        public static BinaryData FromObjectAsJson<T>(T obj)
        {
            return BinaryData.FromString(JsonConvert.SerializeObject(obj), "application/json");
        }

        /// <summary>
        /// Deserialize a BinaryData to an object of the specified type (JSON format)
        /// </summary>
        /// <typeparam name="T">Target object type</typeparam>
        /// <param name="data">BinaryData to deserialize</param>
        /// <returns>Deserialized object</returns>
        public static T? ToObjectFromJson<T>(this BinaryData data)
        {
            return JsonConvert.DeserializeObject<T>(data.ToString());
        }

        /// <summary>
        /// Try to deserialize a BinaryData to an object of the specified type, automatically selecting the serialization method based on MediaType
        /// </summary>
        /// <typeparam name="T">Target object type</typeparam>
        /// <param name="data">BinaryData to deserialize</param>
        /// <param name="result">Deserialization result</param>
        /// <returns>Whether deserialization is successful</returns>
        public static bool TryToObject<T>(this BinaryData data, out T? result)
        {
            try
            {
                if (
                    data.MediaType == "application/x-msgpack"
                    || data.MediaType == "application/x-msgpack-compressed"
                )
                {
                    result = data.ToObjectFromMessagePack<T>();
                    return true;
                }
                else
                {
                    result = data.ToObjectFromJson<T>();
                    return true;
                }
            }
            catch
            {
                result = default;
                return false;
            }
        }

        /// <summary>
        /// Compress a byte array
        /// </summary>
        /// <param name="bytes">Byte array to compress</param>
        /// <returns>Compressed byte array</returns>
        private static byte[] CompressBytes(byte[] bytes)
        {
            using var outputStream = new MemoryStream();
            using (var gzipStream = new GZipStream(outputStream, CompressionLevel.Optimal))
            {
                gzipStream.Write(bytes, 0, bytes.Length);
            }

            return outputStream.ToArray();
        }

        /// <summary>
        /// Decompress a byte array
        /// </summary>
        /// <param name="bytes">Byte array to decompress</param>
        /// <returns>Decompressed byte array</returns>
        private static byte[] DecompressBytes(byte[] bytes)
        {
            using var inputStream = new MemoryStream(bytes);
            using var outputStream = new MemoryStream();
            using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
            {
                gzipStream.CopyTo(outputStream);
            }

            return outputStream.ToArray();
        }
    }
}
