package androidx.core.content;

import android.content.Context;

/**
 * 扫码库 zxing-android-embedded 4.3.0 运行时调用此方法，但其 POM 未声明 androidx.core 依赖，
 * 本项目也不使用 AndroidX（gradle.properties 中 useAndroidX=false）。minSdk 26 时直接转发系统 API。
 * 若以后引入真正的 androidx.core，删除本文件和同目录的 ActivityCompat，否则会报重复类。
 */
public final class ContextCompat {
    private ContextCompat() {
    }

    public static int checkSelfPermission(Context context, String permission) {
        return context.checkSelfPermission(permission);
    }
}
