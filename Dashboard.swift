import Foundation

// The phone page and its service worker and manifest live in web/, shared by every computer
// version of Lid Awake (Mac and Windows serve the same files). build.sh copies web/ into the
// app; these read them once.
func webFile(_ name: String) -> Data? {
    guard let dot = name.lastIndex(of: "."),
          let url = Bundle.main.url(forResource: String(name[..<dot]), withExtension: String(name[name.index(after: dot)...]),
                                    subdirectory: "web") else { return nil }
    return try? Data(contentsOf: url)
}

let dashboardHTML: String = webFile("index.html").flatMap { String(data: $0, encoding: .utf8) }
    ?? "<!doctype html><meta charset=\"utf-8\"><p>Lid Awake's phone page is missing. Run build.sh again.</p>"
