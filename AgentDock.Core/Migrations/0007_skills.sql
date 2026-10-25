CREATE TABLE dbo.SkillFile (
    Id uniqueidentifier NOT NULL CONSTRAINT PK_SkillFile PRIMARY KEY,
    AccountId uniqueidentifier NOT NULL,
    FileName nvarchar(200) NOT NULL,
    DisplayName nvarchar(200) NOT NULL,
    Description nvarchar(1000) NULL,
    Content nvarchar(max) NOT NULL,
    ContentSha256 binary(32) NOT NULL,
    SizeBytes int NOT NULL,
    Version int NOT NULL CONSTRAINT DF_SkillFile_Version DEFAULT 1,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_SkillFile_Created DEFAULT SYSUTCDATETIME(),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_SkillFile_Updated DEFAULT SYSUTCDATETIME(),
    DeletedUtc datetime2(3) NULL,
    CONSTRAINT UQ_SkillFile_AccountId_Id UNIQUE (AccountId, Id),
    CONSTRAINT FK_SkillFile_Account FOREIGN KEY (AccountId) REFERENCES dbo.Account(Id) ON DELETE CASCADE
);
GO
CREATE UNIQUE INDEX UX_SkillFile_Account_FileName ON dbo.SkillFile(AccountId, FileName) WHERE DeletedUtc IS NULL;
GO
CREATE TABLE dbo.AgentSkill (
    AccountId uniqueidentifier NOT NULL,
    AgentId uniqueidentifier NOT NULL,
    SkillFileId uniqueidentifier NOT NULL,
    SortOrder int NOT NULL,
    CONSTRAINT PK_AgentSkill PRIMARY KEY (AgentId, SkillFileId),
    CONSTRAINT FK_AgentSkill_Agent FOREIGN KEY (AccountId, AgentId) REFERENCES dbo.Agent(AccountId, Id) ON DELETE CASCADE,
    CONSTRAINT FK_AgentSkill_Skill FOREIGN KEY (AccountId, SkillFileId) REFERENCES dbo.SkillFile(AccountId, Id)
);
GO
