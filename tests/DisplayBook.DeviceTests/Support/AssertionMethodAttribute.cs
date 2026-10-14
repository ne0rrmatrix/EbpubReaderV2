namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Marks a helper that fails the test when its condition doesn't hold, so analyzers that look
/// for assertions (Sonar's S2699 matches this attribute by name) count a call to it as one.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
sealed class AssertionMethodAttribute : Attribute;
