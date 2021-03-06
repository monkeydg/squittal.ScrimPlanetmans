USE [PlanetmansDbContext]
GO

IF EXISTS ( SELECT *
              FROM INFORMATION_SCHEMA.TABLES
              WHERE TABLE_NAME = N'Loadout' )
BEGIN
    TRUNCATE TABLE [dbo].[Loadout];

    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (1, 2, 2, N'NC Infiltrator')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (3, 4, 2, N'NC Light Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (4, 5, 2, N'NC Medic')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (5, 6, 2, N'NC Engineer')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (6, 7, 2, N'NC Heavy Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (7, 8, 2, N'NC MAX')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (8, 10, 3, N'TR Infiltrator')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (10, 12, 3, N'TR Light Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (11, 13, 3, N'TR Medic')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (12, 14, 3, N'TR Engineer')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (13, 15, 3, N'TR Heavy Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (14, 16, 3, N'TR MAX')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (15, 17, 1, N'VS Infiltrator')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (17, 19, 1, N'VS Light Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (18, 20, 1, N'VS Medic')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (19, 21, 1, N'VS Engineer')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (20, 22, 1, N'VS Heavy Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (21, 23, 1, N'VS MAX')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (28, 190, 4, N'NS Infiltrator')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (29, 191, 4, N'NS Light Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (30, 192, 4, N'NS Combat Medic')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (31, 193, 4, N'NS Engineer')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (32, 194, 4, N'NS Heavy Assault')
    INSERT [dbo].[Loadout] ([Id], [ProfileId], [FactionId], [CodeName]) VALUES (45, 252, 4, N'NS Defector')
END;