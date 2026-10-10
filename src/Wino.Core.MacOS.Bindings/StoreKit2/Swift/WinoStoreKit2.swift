// C entry points over StoreKit 2 for Wino.Core.MacOS.Bindings (StoreKit2Native.cs).
//
// StoreKit 2 is Swift-only, so the .NET macOS bindings cannot reach it. Every export takes a C
// callback and an opaque context pointer and answers once with a UTF-8 JSON envelope:
//   {"ok":true,"value":...}  or  {"ok":false,"error":"..."}
// The JSON string is only valid during the callback; the managed side copies it.
//
// Rebuild the framework with ../../build-native.sh StoreKit2 after changing this file.

import AppKit
import Foundation
import StoreKit

public typealias WinoCallback = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?) -> Void

/// A callback and its context, carried across concurrency domains as a bit pattern.
private struct Reply: Sendable {
    let callback: WinoCallback
    let context: UInt

    init(_ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
        self.callback = callback
        self.context = UInt(bitPattern: context)
    }

    func send(_ json: String) {
        json.withCString { callback(UnsafeMutableRawPointer(bitPattern: context), $0) }
    }

    func value<T: Encodable>(_ value: T) {
        send(Envelope.encode(value))
    }

    func fail(_ error: Error) {
        send(Envelope.encodeError(String(describing: error)))
    }

    func fail(_ message: String) {
        send(Envelope.encodeError(message))
    }
}

private enum Envelope {
    private struct Success<T: Encodable>: Encodable {
        let ok = true
        let value: T
    }

    private struct Failure: Encodable {
        let ok = false
        let error: String
    }

    private static func makeEncoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        return encoder
    }

    static func encode<T: Encodable>(_ value: T) -> String {
        do {
            return String(decoding: try makeEncoder().encode(Success(value: value)), as: UTF8.self)
        } catch {
            return encodeError("Encoding failed: \(error)")
        }
    }

    static func encodeError(_ message: String) -> String {
        let data = (try? makeEncoder().encode(Failure(error: message))) ?? Data(#"{"ok":false,"error":"unknown"}"#.utf8)
        return String(decoding: data, as: UTF8.self)
    }
}

// MARK: - Models

/// Stable names for the managed enum; StoreKit's raw values are display strings.
private func productTypeName(_ type: Product.ProductType) -> String {
    switch type {
    case .consumable: "consumable"
    case .nonConsumable: "nonConsumable"
    case .autoRenewable: "autoRenewable"
    case .nonRenewable: "nonRenewable"
    default: "unknown"
    }
}

private func periodUnitName(_ unit: Product.SubscriptionPeriod.Unit) -> String {
    switch unit {
    case .day: "day"
    case .week: "week"
    case .month: "month"
    case .year: "year"
    @unknown default: "unknown"
    }
}

private struct StorefrontDto: Encodable {
    let id: String
    let countryCode: String
}

private struct ProductDto: Encodable {
    let id: String
    let type: String
    let displayName: String
    let description: String
    let displayPrice: String
    let price: String
    let currencyCode: String?
    let subscriptionPeriodUnit: String?
    let subscriptionPeriodValue: Int?
    let subscriptionGroupId: String?
    let isFamilyShareable: Bool

    init(_ product: Product) {
        id = product.id
        type = productTypeName(product.type)
        displayName = product.displayName
        description = product.description
        displayPrice = product.displayPrice
        price = "\(product.price)"
        currencyCode = product.priceFormatStyle.currencyCode
        subscriptionPeriodUnit = product.subscription.map { periodUnitName($0.subscriptionPeriod.unit) }
        subscriptionPeriodValue = product.subscription?.subscriptionPeriod.value
        subscriptionGroupId = product.subscription?.subscriptionGroupID
        isFamilyShareable = product.isFamilyShareable
    }
}

private struct TransactionDto: Encodable {
    let id: UInt64
    let originalId: UInt64
    let productId: String
    let productType: String
    let purchaseDate: Date
    let originalPurchaseDate: Date
    let expirationDate: Date?
    let revocationDate: Date?
    let isUpgraded: Bool
    let appAccountToken: String?
    let environment: String
    let storefrontCountryCode: String
    /// The signed transaction as the App Store issued it. The Wino Account service verifies it.
    let jws: String
    /// False when StoreKit could not verify the signature on device.
    let isVerified: Bool
    let verificationError: String?

    init(_ result: VerificationResult<Transaction>) {
        let transaction: Transaction
        switch result {
        case .verified(let value):
            transaction = value
            isVerified = true
            verificationError = nil
        case .unverified(let value, let error):
            transaction = value
            isVerified = false
            verificationError = String(describing: error)
        }

        id = transaction.id
        originalId = transaction.originalID
        productId = transaction.productID
        productType = productTypeName(transaction.productType)
        purchaseDate = transaction.purchaseDate
        originalPurchaseDate = transaction.originalPurchaseDate
        expirationDate = transaction.expirationDate
        revocationDate = transaction.revocationDate
        isUpgraded = transaction.isUpgraded
        appAccountToken = transaction.appAccountToken?.uuidString
        environment = transaction.environment.rawValue
        storefrontCountryCode = transaction.storefront.countryCode
        jws = result.jwsRepresentation
    }
}

private struct PurchaseResultDto: Encodable {
    /// success, pending or userCancelled.
    let status: String
    let transaction: TransactionDto?
}

// MARK: - Exports

/// The App Store storefront of the signed-in Apple Account, or null when none is available.
@_cdecl("wino_sk2_storefront")
public func wino_sk2_storefront(_ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    Task {
        let storefront = await Storefront.current
        reply.value(storefront.map { StorefrontDto(id: $0.id, countryCode: $0.countryCode) })
    }
}

/// Loads products by identifier. `idsJson` is a JSON array of strings. Unknown identifiers are omitted.
@_cdecl("wino_sk2_products")
public func wino_sk2_products(_ idsJson: UnsafePointer<CChar>, _ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    guard let ids = try? JSONDecoder().decode([String].self, from: Data(String(cString: idsJson).utf8)) else {
        reply.fail("Product identifiers must be a JSON array of strings.")
        return
    }

    Task {
        do {
            reply.value(try await Product.products(for: ids).map(ProductDto.init))
        } catch {
            reply.fail(error)
        }
    }
}

/// Buys a product. `appAccountToken` is a UUID string tying the purchase to a Wino Account, or null.
/// The transaction stays unfinished until the caller passes its id to `wino_sk2_finish`.
@_cdecl("wino_sk2_purchase")
public func wino_sk2_purchase(_ productId: UnsafePointer<CChar>, _ appAccountToken: UnsafePointer<CChar>?,
                              _ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    let id = String(cString: productId)
    var options: Set<Product.PurchaseOption> = []
    if let appAccountToken {
        guard let token = UUID(uuidString: String(cString: appAccountToken)) else {
            reply.fail("The app account token must be a UUID.")
            return
        }
        options.insert(.appAccountToken(token))
    }

    Task { @MainActor in
        do {
            guard let product = try await Product.products(for: [id]).first else {
                reply.fail("Product \(id) was not found in the App Store.")
                return
            }

            switch try await product.purchase(options: options) {
            case .success(let result):
                reply.value(PurchaseResultDto(status: "success", transaction: TransactionDto(result)))
            case .pending:
                reply.value(PurchaseResultDto(status: "pending", transaction: nil))
            case .userCancelled:
                reply.value(PurchaseResultDto(status: "userCancelled", transaction: nil))
            @unknown default:
                reply.fail("Unknown purchase result.")
            }
        } catch {
            reply.fail(error)
        }
    }
}

/// The latest transaction for every product the user is entitled to.
@_cdecl("wino_sk2_current_entitlements")
public func wino_sk2_current_entitlements(_ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    Task {
        var transactions: [TransactionDto] = []
        for await result in Transaction.currentEntitlements {
            transactions.append(TransactionDto(result))
        }
        reply.value(transactions)
    }
}

/// Transactions that were delivered but not finished yet.
@_cdecl("wino_sk2_unfinished")
public func wino_sk2_unfinished(_ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    Task {
        var transactions: [TransactionDto] = []
        for await result in Transaction.unfinished {
            transactions.append(TransactionDto(result))
        }
        reply.value(transactions)
    }
}

/// Finishes an unfinished transaction after its content was granted. Answers true when it was found.
@_cdecl("wino_sk2_finish")
public func wino_sk2_finish(_ transactionId: UInt64, _ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    Task {
        for await result in Transaction.unfinished {
            let transaction = result.unsafePayloadValue
            if transaction.id == transactionId {
                await transaction.finish()
                reply.value(true)
                return
            }
        }
        reply.value(false)
    }
}

/// Restore Purchases: syncs transactions with the App Store. May ask the user to sign in.
@_cdecl("wino_sk2_sync")
public func wino_sk2_sync(_ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    let reply = Reply(callback, context)
    Task { @MainActor in
        do {
            try await AppStore.sync()
            reply.value(true)
        } catch {
            reply.fail(error)
        }
    }
}

private let updatesListener = UpdatesListener()

private final class UpdatesListener: @unchecked Sendable {
    private let lock = NSLock()
    private var task: Task<Void, Never>?

    func start(_ reply: Reply) {
        lock.lock()
        defer { lock.unlock() }
        task?.cancel()
        task = Task.detached {
            for await result in Transaction.updates {
                reply.value(TransactionDto(result))
            }
        }
    }

    func stop() {
        lock.lock()
        defer { lock.unlock() }
        task?.cancel()
        task = nil
    }
}

/// Starts listening for transactions that arrive outside a purchase call: renewals, refunds,
/// Ask to Buy approvals and purchases made on other devices. The callback runs once per
/// transaction until `wino_sk2_stop_updates`, so the context must stay valid until then.
/// Starting again replaces the previous listener.
@_cdecl("wino_sk2_start_updates")
public func wino_sk2_start_updates(_ callback: WinoCallback, _ context: UnsafeMutableRawPointer?) {
    updatesListener.start(Reply(callback, context))
}

@_cdecl("wino_sk2_stop_updates")
public func wino_sk2_stop_updates() {
    updatesListener.stop()
}

/// Asks the system to show the rating prompt over the key window. The system decides whether it
/// appears (at most three times a year) and reports nothing back.
@_cdecl("wino_sk2_request_review")
public func wino_sk2_request_review() {
    Task { @MainActor in
        let window = NSApp.keyWindow ?? NSApp.mainWindow ?? NSApp.windows.first(where: { $0.isVisible })
        guard let controller = window?.contentViewController else { return }
        AppStore.requestReview(in: controller)
    }
}
