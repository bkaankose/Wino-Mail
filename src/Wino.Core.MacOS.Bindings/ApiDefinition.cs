namespace Wino.Core.MacOS.Bindings;

// The binding generator requires an API definition, but every module here is a Swift framework
// reached through C exports and [LibraryImport], so there are no Objective-C types to declare.
// Being a binding project is what packages the NativeReference frameworks for the app bundle.
internal interface NoObjectiveCApi;
