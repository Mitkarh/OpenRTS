using OpenRTS.Core.Visibility;

namespace OpenRTS.Godot.Rendering;

public enum ContactDisplayStyle { Icons, BuildingModels, IdentifiedModels }

public static class ContactDisplay
{
    public static bool ShowsModel(ContactKnowledge contact, ContactDisplayStyle style) =>
        contact.UnitType.HasValue && (style == ContactDisplayStyle.IdentifiedModels ||
            style == ContactDisplayStyle.BuildingModels && contact.Class == ContactClass.Building);
}
