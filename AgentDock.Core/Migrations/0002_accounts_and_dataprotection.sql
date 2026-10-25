CREATE TABLE dbo.DataProtectionKeys (
    Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_DataProtectionKeys PRIMARY KEY,
    FriendlyName nvarchar(max) NULL,
    Xml nvarchar(max) NULL
);
GO
CREATE TABLE dbo.Account (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_Account PRIMARY KEY,
    OwnerUserId nvarchar(450) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    Status nvarchar(20) NOT NULL CONSTRAINT DF_Account_Status DEFAULT 'Active',
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Account_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_Account_Updated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_Account_Status CHECK (Status IN ('Active','Suspended')),
    CONSTRAINT FK_Account_AspNetUsers FOREIGN KEY (OwnerUserId) REFERENCES dbo.AspNetUsers(Id) ON DELETE CASCADE
);
GO
CREATE UNIQUE INDEX UX_Account_OwnerUserId ON dbo.Account(OwnerUserId);
GO
