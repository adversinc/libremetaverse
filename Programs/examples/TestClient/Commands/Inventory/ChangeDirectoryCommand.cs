﻿using System.Collections.Generic;

namespace OpenMetaverse.TestClient.Commands.Inventory.Shell
{
    public class ChangeDirectoryCommand : Command
    {
        private OpenMetaverse.Inventory Inventory;

        public ChangeDirectoryCommand(TestClient client)
        {
            Name = "cd";
            Description = "Changes the current working inventory folder.";
            Category = CommandCategory.Inventory;
        }
        public override string Execute(string[] args, UUID fromAgentID)
        {
            Inventory = Client.Inventory.Store;

            if (args.Length > 1)
                return "Usage: cd [path-to-folder]";
            string pathStr = "";
            string[] path = null;
            if (args.Length == 0)
            {
                path = new string[] { "" };
                // cd without any arguments doesn't do anything.
            }
            else if (args.Length == 1)
            {
                pathStr = args[0];
                path = pathStr.Split(new char[] { '/' });
                // Use '/' as a path seperator.
            }
            InventoryFolder currentFolder = Client.CurrentDirectory;
            if (pathStr.StartsWith("/"))
                currentFolder = Inventory.RootFolder;

            if (currentFolder == null) // We need this to be set to something. 
                return "Error: Client not logged in.";

            // Traverse the path, looking for the 
            foreach (var nextName in path)
            {
                if (string.IsNullOrEmpty(nextName) || nextName == ".")
                    continue; // Ignore '.' and blanks, stay in the current directory.
                if (nextName == ".." && !currentFolder.Equals((InventoryBase) Inventory.RootFolder))
                {
                    // If we encounter .., move to the parent folder.
                    currentFolder = Inventory[currentFolder.ParentUUID] as InventoryFolder;
                }
                else
                {
                    List<InventoryBase> currentContents = Inventory.GetContents(currentFolder);
                    // Try and find an InventoryBase with the corresponding name.
                    bool found = false;
                    foreach (InventoryBase item in currentContents)
                    {
                        // Allow lookup by UUID as well as name:
                        if (item.Name == nextName || item.UUID.ToString() == nextName)
                        {
                            found = true;
                            if (item is InventoryFolder folder)
                            {
                                currentFolder = folder;
                            }
                            else
                            {
                                return item.Name + " is not a folder.";
                            }
                        }
                    }
                    if (!found)
                        return nextName + " not found in " + currentFolder.Name;
                }
            }
            Client.CurrentDirectory = currentFolder;
            return "Current folder: " + currentFolder.Name;
        }
    }
}