using System;
using System.Text.Json;
using ReboundGuardTests;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            runtime = Environment.Version.ToString(),
            queue_membership = QueueMembershipTests.Run(),
            frame_health = FrameHealthTests.Run(),
            bucket_stamp = BucketStampTests.Run()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
