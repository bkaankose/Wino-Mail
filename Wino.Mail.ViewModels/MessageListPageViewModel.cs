﻿using System;
using System.Collections.Generic;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.ViewModels
{
    public class MessageListPageViewModel : BaseViewModel
    {
        public IPreferencesService PreferencesService { get; }

        private int selectedMarkAsOptionIndex;

        public int SelectedMarkAsOptionIndex
        {
            get => selectedMarkAsOptionIndex;
            set
            {
                if (SetProperty(ref selectedMarkAsOptionIndex, value))
                {
                    if (value >= 0)
                    {
                        PreferencesService.MarkAsPreference = (MailMarkAsOption)Enum.GetValues(typeof(MailMarkAsOption)).GetValue(value);
                    }
                }
            }
        }

        private readonly List<MailOperation> availableHoverActions =
        [
            MailOperation.Archive,
            MailOperation.SoftDelete,
            MailOperation.SetFlag,
            MailOperation.MarkAsRead,
            MailOperation.MoveToJunk
        ];

        public List<string> AvailableHoverActionsTranslations { get; set; } =
        [
            Translator.HoverActionOption_Archive,
            Translator.HoverActionOption_Delete,
            Translator.HoverActionOption_ToggleFlag,
            Translator.HoverActionOption_ToggleRead,
            Translator.HoverActionOption_MoveJunk
        ];

        #region Properties

        private int leftHoverActionIndex;

        public int LeftHoverActionIndex
        {
            get => leftHoverActionIndex;
            set
            {
                if (SetProperty(ref leftHoverActionIndex, value))
                {
                    PreferencesService.LeftHoverAction = availableHoverActions[value];
                }
            }
        }


        private int centerHoverActionIndex;

        public int CenterHoverActionIndex
        {
            get => centerHoverActionIndex;
            set
            {
                if (SetProperty(ref centerHoverActionIndex, value))
                {
                    PreferencesService.CenterHoverAction = availableHoverActions[value];
                }
            }
        }

        private int rightHoverActionIndex;

        public int RightHoverActionIndex
        {
            get => rightHoverActionIndex;
            set
            {
                if (SetProperty(ref rightHoverActionIndex, value))
                {
                    PreferencesService.RightHoverAction = availableHoverActions[value];
                }
            }
        }

        #endregion

        public MessageListPageViewModel(IDialogService dialogService,
                                        IPreferencesService preferencesService) : base(dialogService)
        {
            PreferencesService = preferencesService;

            leftHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.LeftHoverAction);
            centerHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.CenterHoverAction);
            rightHoverActionIndex = availableHoverActions.IndexOf(PreferencesService.RightHoverAction);

            SelectedMarkAsOptionIndex = Array.IndexOf(Enum.GetValues(typeof(MailMarkAsOption)), PreferencesService.MarkAsPreference);
        }
    }
}
