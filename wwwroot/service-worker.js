self.addEventListener("install", (event) => {
  event.waitUntil(self.skipWaiting());
});

self.addEventListener("activate", (event) => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener("fetch", (event) => {
  if (new URL(event.request.url).origin === self.location.origin) {
    event.respondWith(fetch(event.request));
  }
});

self.addEventListener("push", (event) => {
  let notification = {
    title: "Hora do devocional",
    body: "Seu momento de hoje ainda não foi registrado. Separe alguns minutos para a Palavra.",
    url: "/"
  };

  if (event.data) {
    try {
      notification = { ...notification, ...event.data.json() };
    } catch {
      notification.body = event.data.text();
    }
  }

  event.waitUntil(self.registration.showNotification(notification.title, {
    body: notification.body,
    icon: "/pwa-icon-192.png",
    badge: "/pwa-icon-192.png",
    tag: "devocional-diario",
    data: { url: notification.url }
  }));
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = new URL(event.notification.data?.url || "/", self.location.origin).href;
  event.waitUntil(self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((clients) => {
    for (const client of clients) {
      if (client.url === url && "focus" in client) {
        return client.focus();
      }
    }
    return self.clients.openWindow(url);
  }));
});
