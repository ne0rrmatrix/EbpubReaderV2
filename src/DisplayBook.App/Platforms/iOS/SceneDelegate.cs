using Foundation;

namespace DisplayBook.App;

// Named by UISceneDelegateClassName in Info.plist: creates the app's window under the UIKit scene life cycle,
// which apps built with the iOS and Mac Catalyst 27 SDK must use (https://aka.ms/uiscene).
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
