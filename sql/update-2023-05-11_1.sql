ALTER TABLE `jobs` ADD FOREIGN KEY (`characterId`) REFERENCES `characters`(`characterId`) ON DELETE CASCADE ON UPDATE CASCADE;