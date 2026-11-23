package com.codex.phonedeck;

import org.junit.Test;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;

import static org.junit.Assert.assertEquals;
import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertNotNull;
import static org.junit.Assert.assertTrue;

public class ShortcutConfigRepositoryTest {
    @Test
    public void fullConfigurationKeepsAllCustomButtonsWithoutResetOrNewEntries() {
        ArrayList<ShortcutButtonConfig> values = customButtons(100);
        ArrayList<String> idsBefore = ids(values);
        ArrayList<Boolean> visibilityBefore = visibility(values);

        assertFalse(ShortcutConfigRepository.migrateBuiltinButtons(values));

        assertEquals(100, values.size());
        assertEquals(idsBefore, ids(values));
        assertEquals(visibilityBefore, visibility(values));
    }

    @Test
    public void migrationAddsOnlyWithinCapacityAndKeepsExistingOrderAndHiddenState() {
        ArrayList<ShortcutButtonConfig> values = customButtons(97);
        values.get(1).visible = false;
        ArrayList<String> idsBefore = ids(values);

        assertTrue(ShortcutConfigRepository.migrateBuiltinButtons(values));

        assertEquals(100, values.size());
        assertEquals(idsBefore, ids(values.subList(0, idsBefore.size())));
        assertFalse(values.get(1).visible);
        assertEquals("delete", values.get(97).id);
        assertEquals("slashPlan", values.get(98).id);
        assertEquals("slashGoal", values.get(99).id);
        assertFalse(containsId(values, "slashCompact"));
    }

    @Test
    public void retiredBuiltInFreesOneSlotForIntroducedBuiltIn() {
        ArrayList<ShortcutButtonConfig> values = customButtons(99);
        values.add(0, button("agentPlan", "规划", true, true));

        assertTrue(ShortcutConfigRepository.migrateBuiltinButtons(values));

        assertEquals(100, values.size());
        assertFalse(containsId(values, "agentPlan"));
        assertEquals("delete", values.get(99).id);
        assertTrue(containsId(values, "custom-0"));
        assertTrue(containsId(values, "custom-98"));
    }

    @Test
    public void customButtonWithRetiredIdIsPreserved() {
        ArrayList<ShortcutButtonConfig> values = new ArrayList<>();
        values.add(button("agentPlan", "我的规划", false, false));

        assertTrue(ShortcutConfigRepository.migrateBuiltinButtons(values));

        ShortcutButtonConfig preserved = find(values, "agentPlan");
        assertNotNull(preserved);
        assertFalse(preserved.builtIn);
        assertFalse(preserved.visible);
    }

    private static ArrayList<ShortcutButtonConfig> customButtons(int count) {
        ArrayList<ShortcutButtonConfig> values = new ArrayList<>();
        for (int index = 0; index < count; index++) {
            values.add(button("custom-" + index, "自定义" + index, false, true));
        }
        return values;
    }

    private static ShortcutButtonConfig button(
            String id, String label, boolean builtIn, boolean visible) {
        return new ShortcutButtonConfig(
                id, label, "", "blue", visible, 0, builtIn,
                Arrays.asList("F1"), 45, 0);
    }

    private static ArrayList<String> ids(List<ShortcutButtonConfig> values) {
        ArrayList<String> result = new ArrayList<>();
        for (ShortcutButtonConfig value : values) {
            result.add(value.id);
        }
        return result;
    }

    private static ArrayList<Boolean> visibility(List<ShortcutButtonConfig> values) {
        ArrayList<Boolean> result = new ArrayList<>();
        for (ShortcutButtonConfig value : values) {
            result.add(value.visible);
        }
        return result;
    }

    private static boolean containsId(List<ShortcutButtonConfig> values, String id) {
        return find(values, id) != null;
    }

    private static ShortcutButtonConfig find(List<ShortcutButtonConfig> values, String id) {
        for (ShortcutButtonConfig value : values) {
            if (id.equals(value.id)) {
                return value;
            }
        }
        return null;
    }
}
