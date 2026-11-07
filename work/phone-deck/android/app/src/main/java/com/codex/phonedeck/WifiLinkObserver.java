package com.codex.phonedeck;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.LinkAddress;
import android.net.LinkProperties;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkRequest;
import android.net.RouteInfo;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import java.net.InetAddress;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Process-local Wi-Fi link lifecycle owner.
 *
 * <p>Both the Activity and the foreground shared-audio service subscribe to
 * this one callback registration.  The observer only watches Wi-Fi networks;
 * cellular and VPN callbacks never create a generation.  A generation is
 * advanced for a Wi-Fi identity change, IP/route change, or loss.  The
 * observer invalidates the shared {@link ConnectionMonitor} once per event,
 * then notifies all subscribers so each owner can schedule its own probe.</p>
 */
final class WifiLinkObserver {
    interface Listener {
        void onWifiLinkChanged(State state);
    }

    static final class State {
        final long generation;
        final boolean connected;
        final String signature;
        final String reason;

        State(long generation, boolean connected, String signature, String reason) {
            this.generation = generation;
            this.connected = connected;
            this.signature = signature;
            this.reason = reason;
        }
    }

    static final class Registration implements AutoCloseable {
        private WifiLinkObserver owner;
        private final Listener listener;

        private Registration(WifiLinkObserver owner, Listener listener) {
            this.owner = owner;
            this.listener = listener;
        }

        @Override
        public void close() {
            WifiLinkObserver current = owner;
            if (current != null) {
                owner = null;
                current.remove(listener);
            }
        }
    }

    // register() converts every owner to its application context before it
    // reaches this singleton; Activity/Service contexts are never retained.
    @android.annotation.SuppressLint("StaticFieldLeak")
    private static final WifiLinkObserver INSTANCE = new WifiLinkObserver();

    private final Object lock = new Object();
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Map<Listener, Boolean> listeners = new HashMap<>();
    private final Map<Network, Boolean> matchingNetworks = new HashMap<>();
    private WifiLinkPolicy.ActiveNetworkTracker activeTracker =
            new WifiLinkPolicy.ActiveNetworkTracker();
    private long callbackEpoch;
    private volatile long generation;
    private Context context;
    private ConnectivityManager connectivityManager;
    private ConnectivityManager.NetworkCallback networkCallback;

    private WifiLinkObserver() {
    }

    static Registration register(Context context, Listener listener) {
        if (context == null || listener == null) {
            throw new IllegalArgumentException("context/listener must not be null");
        }
        INSTANCE.add(context.getApplicationContext(), listener);
        return new Registration(INSTANCE, listener);
    }

    static long currentGeneration() {
        return INSTANCE.generation;
    }

    private void add(Context context, Listener listener) {
        synchronized (lock) {
            boolean wasEmpty = listeners.isEmpty();
            listeners.put(listener, Boolean.TRUE);
            if (wasEmpty || networkCallback == null) {
                startLocked(context);
            }
        }
    }

    private void remove(Listener listener) {
        synchronized (lock) {
            listeners.remove(listener);
            if (listeners.isEmpty()) {
                stopLocked();
            }
        }
    }

    private void startLocked(Context context) {
        this.context = context;
        connectivityManager = (ConnectivityManager) context.getSystemService(
                Context.CONNECTIVITY_SERVICE);
        if (connectivityManager == null) {
            Log.w("PhoneDeckNet", "无法获取 ConnectivityManager");
            return;
        }
        final long epoch = ++callbackEpoch;
        networkCallback = new ConnectivityManager.NetworkCallback() {
            @Override
            public void onAvailable(Network network) {
                // Android guarantees ordered capabilities/link-properties
                // callbacks after this.  Do not synchronously query either
                // object here; the later callbacks carry the authoritative
                // values.
                post(epoch, () -> onAvailableLocked(network));
            }

            @Override
            public void onCapabilitiesChanged(Network network,
                                               NetworkCapabilities capabilities) {
                post(epoch, () -> onCapabilitiesChangedLocked(network, capabilities));
            }

            @Override
            public void onLinkPropertiesChanged(Network network,
                                                LinkProperties linkProperties) {
                post(epoch, () -> onLinkPropertiesChangedLocked(network, linkProperties));
            }

            @Override
            public void onLost(Network network) {
                post(epoch, () -> onLostLocked(network));
            }
        };
        try {
            // LAN access must continue to work on a Wi-Fi network without
            // validated Internet.  Transport filtering excludes cellular and
            // VPN callbacks while retaining local-only Wi-Fi.
            NetworkRequest request = new NetworkRequest.Builder()
                    .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                    .build();
            // API 26+ (the app minSdk) lets us keep callback processing on
            // the main handler; all consumers already own main-thread state.
            connectivityManager.registerNetworkCallback(request, networkCallback, handler);
        } catch (Exception exception) {
            Log.w("PhoneDeckNet", "注册 Wi-Fi 网络回调失败：" + exception.getMessage());
            networkCallback = null;
            return;
        }
    }

    private void stopLocked() {
        ++callbackEpoch;
        ConnectivityManager manager = connectivityManager;
        ConnectivityManager.NetworkCallback callback = networkCallback;
        connectivityManager = null;
        networkCallback = null;
        matchingNetworks.clear();
        activeTracker = new WifiLinkPolicy.ActiveNetworkTracker();
        // Never reuse an observer generation after the last owner closes and
        // a new Activity/service subscribes. The reducer can restart, but a
        // probe token must remain invalid across registration lifetimes.
        generation++;
        if (manager != null && callback != null) {
            try {
                manager.unregisterNetworkCallback(callback);
            } catch (Exception ignored) {
                // The system may have already torn down the callback.
            }
        }
    }

    private void post(long epoch, Runnable action) {
        handler.post(() -> {
            synchronized (lock) {
                if (epoch != callbackEpoch || listeners.isEmpty() || networkCallback == null) {
                    return;
                }
            }
            action.run();
        });
    }

    private void onAvailableLocked(Network network) {
        if (network == null) {
            return;
        }
        synchronized (lock) {
            if (!isCallbackActiveLocked()) {
                return;
            }
            matchingNetworks.put(network, Boolean.TRUE);
        }
        publish(network, "available", "", true);
    }

    private void onCapabilitiesChangedLocked(Network network,
                                             NetworkCapabilities capabilities) {
        if (network == null || capabilities == null) {
            return;
        }
        boolean relevant = WifiLinkPolicy.isRelevantTransport(
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI),
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN));
        synchronized (lock) {
            if (!isCallbackActiveLocked()) {
                return;
            }
            if (relevant) {
                matchingNetworks.put(network, Boolean.TRUE);
            } else {
                matchingNetworks.remove(network);
            }
        }
        if (relevant) {
            // Capabilities can change without changing the link identity or
            // routes.  The callback still confirms this Wi-Fi network remains
            // the one whose link properties we track.
            publish(network, "capabilities", null, false);
        } else {
            clearIfActive(network, "transport-changed");
            promoteReplacement();
        }
    }

    private void onLinkPropertiesChangedLocked(Network network,
                                               LinkProperties linkProperties) {
        if (network == null || linkProperties == null) {
            return;
        }
        synchronized (lock) {
            if (!isCallbackActiveLocked() || !matchingNetworks.containsKey(network)) {
                return;
            }
        }
        publish(network, "link-properties", linkSignature(linkProperties), false);
    }

    private void onLostLocked(Network network) {
        if (network == null) {
            return;
        }
        synchronized (lock) {
            matchingNetworks.remove(network);
        }
        boolean wasActive;
        synchronized (lock) {
            wasActive = activeTracker.isActive(network);
        }
        clearIfActive(network, "lost");
        if (wasActive) {
            promoteReplacement();
        }
    }

    private boolean isCallbackActiveLocked() {
        return networkCallback != null && !listeners.isEmpty();
    }

    private void clearIfActive(Network network, String reason) {
        List<Listener> snapshot;
        State state;
        synchronized (lock) {
            if (!isCallbackActiveLocked()) {
                return;
            }
            WifiLinkPolicy.ActiveNetworkTracker.Transition transition =
                    activeTracker.onLost(network);
            if (transition == null) {
                return;
            }
            generation++;
            state = new State(generation, false,
                    transition.signature, reason);
            snapshot = new ArrayList<>(listeners.keySet());
        }
        invalidateAndNotify(state, snapshot);
    }

    private void promoteReplacement() {
        Network replacement = null;
        synchronized (lock) {
            if (!isCallbackActiveLocked() || activeTracker.hasActive()) {
                return;
            }
            for (Network candidate : matchingNetworks.keySet()) {
                replacement = candidate;
                break;
            }
        }
        if (replacement != null) {
            publish(replacement, "available", "", true);
        }
    }

    private void publish(Network network, String reason, String signature,
                         boolean allowNetworkSwitch) {
        List<Listener> snapshot;
        State state;
        synchronized (lock) {
            if (!isCallbackActiveLocked()
                    || !matchingNetworks.containsKey(network)) {
                return;
            }
            // registerNetworkCallback may briefly report two Wi-Fi networks
            // during handover.  Only onAvailable (or a promotion after loss)
            // may switch the active identity; late properties from the old
            // network must not roll the generation back.
            if (!allowNetworkSwitch && activeTracker.hasActive()
                    && !activeTracker.isActive(network)) {
                return;
            }
            WifiLinkPolicy.ActiveNetworkTracker.Transition transition = allowNetworkSwitch
                    ? activeTracker.onAvailable(network)
                    : activeTracker.onLinkProperties(network, signature);
            if (transition == null) {
                return;
            }
            generation++;
            state = new State(generation, true,
                    transition.signature, reason);
            snapshot = new ArrayList<>(listeners.keySet());
        }
        invalidateAndNotify(state, snapshot);
    }

    private void invalidateAndNotify(State state, List<Listener> snapshot) {
        // One reset per observer event, independent of how many owners are
        // currently subscribed.  Activity/service only schedule probes.
        try {
            if (!snapshot.isEmpty()) {
                ConnectionMonitor.get(context).reset();
            }
        } catch (Exception exception) {
            Log.w("PhoneDeckNet", "网络代次失效失败：" + exception.getMessage());
        }
        for (Listener listener : snapshot) {
            try {
                listener.onWifiLinkChanged(state);
            } catch (RuntimeException exception) {
                Log.w("PhoneDeckNet", "Wi-Fi 状态监听器失败：" + exception.getMessage());
            }
        }
    }

    private static String linkSignature(LinkProperties linkProperties) {
        List<String> addresses = new ArrayList<>();
        for (LinkAddress address : linkProperties.getLinkAddresses()) {
            if (address != null && address.getAddress() != null) {
                addresses.add(address.getAddress().getHostAddress() + "/"
                        + address.getPrefixLength());
            }
        }
        List<String> routes = new ArrayList<>();
        for (RouteInfo route : linkProperties.getRoutes()) {
            if (route == null) {
                continue;
            }
            String destination = route.getDestination() == null
                    ? "" : route.getDestination().toString();
            InetAddress gateway = route.getGateway();
            String gatewayText = gateway == null ? "" : gateway.getHostAddress();
            routes.add(destination + " via " + gatewayText + " dev "
                    + String.valueOf(route.getInterface()));
        }
        Collections.sort(addresses);
        Collections.sort(routes);
        return String.valueOf(linkProperties.getInterfaceName())
                + "|a=" + addresses + "|r=" + routes;
    }
}
