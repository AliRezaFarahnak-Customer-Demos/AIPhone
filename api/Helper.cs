using System.Text.Json.Nodes;

public static class Helper
{
    public static JsonObject GetJsonObject(BinaryData data)
    {
        var parsed = JsonNode.Parse(data);
        if (parsed == null) throw new InvalidOperationException("Failed to parse JSON from event data");
        return parsed.AsObject();
    }

    public static string GetCallerId(JsonObject jsonObject)
    {
        try
        {
            var fromNode = jsonObject["from"];
            if (fromNode == null) throw new InvalidOperationException("'from' field not found in event");

            var rawIdNode = fromNode["rawId"];
            if (rawIdNode == null) throw new InvalidOperationException("'from.rawId' field not found in event");

            var rawId = rawIdNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawId)) throw new InvalidOperationException("'from.rawId' is empty");

            // AVAYA FIX: Handle different caller ID formats
            // Avaya might send formats like: "sip:+97470899162@avaya-sbc.example.com"
            // We need to extract just the phone number part
            if (rawId.Contains("@"))
            {
                rawId = rawId.Split('@')[0];
            }

            // Clean up any "sip:" prefix
            if (rawId.StartsWith("sip:"))
            {
                rawId = rawId.Substring(4);
            }

            // If it doesn't start with +, add it (E.164 format)
            if (!rawId.StartsWith("+") && !rawId.StartsWith("4:"))
            {
                rawId = "+" + rawId;
            }

            return rawId;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract caller ID from event: {ex.Message}", ex);
        }
    }

    public static string GetIncomingCallContext(JsonObject jsonObject)
    {
        try
        {
            var contextNode = jsonObject["incomingCallContext"];
            if (contextNode == null) throw new InvalidOperationException("'incomingCallContext' field not found in event");

            var context = contextNode.GetValue<string>();
            if (string.IsNullOrWhiteSpace(context)) throw new InvalidOperationException("IncomingCallContext is empty");

            // JWT tokens have format: header.payload[.signature]
            // Event Grid sends header.payload (no signature, which is correct)
            var parts = context.Split('.');
            if (parts.Length < 2)
            {
                throw new InvalidOperationException($"IncomingCallContext has invalid format. Expected at least 2 parts (header.payload), got {parts.Length}");
            }

            // CRITICAL: Check if context looks corrupted or empty (Avaya SBC issue)
            // Some malformed contexts can pass JWT format check but fail at Azure side
            if (context.Length < 50)
            {
                throw new InvalidOperationException($"IncomingCallContext suspiciously short ({context.Length} chars). Likely corrupted by Avaya SBC.");
            }

            // Try to decode the payload to ensure it's not garbage
            try
            {
                var payload = parts[1];
                // Add padding if needed (JWT payloads may have unpadded base64)
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                var decodedBytes = Convert.FromBase64String(payload);
                if (decodedBytes.Length == 0)
                {
                    throw new InvalidOperationException("Decoded JWT payload is empty");
                }
            }
            catch (Exception decodeEx)
            {
                throw new InvalidOperationException($"IncomingCallContext JWT payload is malformed or corrupted: {decodeEx.Message}");
            }

            return context;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to extract/validate IncomingCallContext: {ex.Message}", ex);
        }
    }
}